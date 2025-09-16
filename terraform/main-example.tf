terraform {
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
  }
  required_version = ">= 1.1.0"
}

provider "azurerm" {
  features {}
}

# -------------------------
# Variables (simple defaults)
# -------------------------
variable "subscription_id" {
  type    = string
  default = "" # optional: set via TF_VAR_subscription_id or terraform.tfvars
}

variable "location" {
  type    = string
  default = "eastus"
}

variable "rg_name" {
  type    = string
  default = "1st-Project"
}

variable "sql_admin" {
  type    = string
  default = "sqladmin"
}

variable "sql_admin_password" {
  type    = string
  description = "Set via environment or terraform.tfvars (sensitive)"
  type    = string
  sensitive = true
  default = ""
}

# -------------------------
# Resource Group
# -------------------------
resource "azurerm_resource_group" "rg" {
  name     = var.rg_name
  location = var.location
  tags = {
    project = "IoTRealtimePipeline"
  }
}

# -------------------------
# ADLS Gen2 (Storage Account)
# -------------------------
resource "azurerm_storage_account" "adls" {
  name                     = lower(replace("${var.rg_name}adls", "_", ""))[0:24] # ensure <=24 chars
  resource_group_name      = azurerm_resource_group.rg.name
  location                 = azurerm_resource_group.rg.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  is_hns_enabled           = true # hierarchical namespace for Data Lake Gen2

  tags = {
    role = "adls-gen2"
  }
}

# -------------------------
# IoT Central (simulator)
# -------------------------
resource "azurerm_iot_central_application" "iotc" {
  name                = "iot-central-app-${random_id.iotc_suffix.hex}"
  resource_group_name = azurerm_resource_group.rg.name
  subdomain           = "iotcentral-${random_id.iotc_suffix.hex}"
  location            = azurerm_resource_group.rg.location
  sku                 = "ST0"
  display_name        = "IoT Central - simulated"
  # NOTE: You may need to configure additional properties via portal or automation
}

resource "random_id" "iotc_suffix" {
  byte_length = 4
}

# -------------------------
# Event Hub Namespace + Event Hub
# -------------------------
resource "azurerm_eventhub_namespace" "eh_ns" {
  name                = "iot-eh-ns-${random_id.eh_suffix.hex}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  sku                 = "Basic"
  capacity            = 1
  tags = {
    role = "eventhub"
  }
}

resource "random_id" "eh_suffix" {
  byte_length = 4
}

resource "azurerm_eventhub" "eh" {
  name                = "iot-telemetry"
  resource_group_name = azurerm_resource_group.rg.name
  namespace_name      = azurerm_eventhub_namespace.eh_ns.name
  partition_count     = 4
  message_retention   = 1
  capture_description {
    # capture is optional; include if you want to persist raw events to storage directly
    enabled = false
  }
}

# -------------------------
# Stream Analytics Job (barebone)
# -------------------------
# NOTE: This creates the job resource only. Use the azurerm_stream_analytics_input_eventhub,
# azurerm_stream_analytics_output_blob, azurerm_stream_analytics_output_sql_database and
# azurerm_stream_analytics_transformation resources to attach inputs/outputs and query.
resource "azurerm_stream_analytics_job" "sa" {
  name                = "iot-data-lake-gen2"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  sku {
    name = "Standard"
  }
  compatibility_level = "1.2"
  events_out_of_order_policy      = "Adjust"
  events_out_of_order_max_delay   = "00:00:30"
  events_late_arrival_max_delay   = "00:01:00"

  tags = {
    role = "stream-analytics"
  }
}

# -------------------------
# Azure SQL Server + Database
# -------------------------
resource "azurerm_sql_server" "sqlserver" {
  name                         = "iot-sql-${random_id.sqlsrv.hex}"
  resource_group_name          = azurerm_resource_group.rg.name
  location                     = azurerm_resource_group.rg.location
  version                      = "12.0"
  administrator_login          = var.sql_admin
  administrator_login_password = var.sql_admin_password
  minimal_tls_version          = "1.2"

  tags = {
    role = "sql-server"
  }
}

resource "random_id" "sqlsrv" {
  byte_length = 4
}

resource "azurerm_sql_database" "telemetry_db" {
  name                = "iot-sql-db"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  server_name         = azurerm_sql_server.sqlserver.name
  sku_name            = "GP_S_Gen5" # adjust to taste
  max_size_gb         = 5

  tags = {
    role = "telemetry-db"
  }
}

# -------------------------
# (Optional) Azure Function App - commented / optional
# -------------------------
# Uncomment and provide required settings if you want Terraform to create the function app.
#
# resource "azurerm_app_service_plan" "function_plan" {
#   name                = "asp-functions-${random_id.fn.hex}"
#   location            = azurerm_resource_group.rg.location
#   resource_group_name = azurerm_resource_group.rg.name
#   kind                = "Linux"
#   reserved            = true
#   sku {
#     tier = "ElasticPremium"
#     size = "EP1"
#   }
# }
#
# resource "random_id" "fn" {
#   byte_length = 4
# }
#
# resource "azurerm_function_app" "function" {
#   name                       = "iot-telemetry-fn"
#   resource_group_name        = azurerm_resource_group.rg.name
#   location                   = azurerm_resource_group.rg.location
#   app_service_plan_id        = azurerm_app_service_plan.function_plan.id
#   storage_account_name       = azurerm_storage_account.adls.name
#   storage_account_access_key = azurerm_storage_account.adls.primary_access_key
#
#   app_settings = {
#     FUNCTIONS_WORKER_RUNTIME = "dotnet-isolated"
#     AzureWebJobsStorage      = azurerm_storage_account.adls.primary_connection_string
#     SQL_CONNECTION_STRING    = "" # set via pipeline/KeyVault
#     POWERBI_PUSH_URL         = ""
#   }
# }

# -------------------------
# Outputs
# -------------------------
output "resource_group_name" {
  value = azurerm_resource_group.rg.name
}

output "storage_account_name" {
  value = azurerm_storage_account.adls.name
}

output "iot_central_app_subdomain" {
  value = azurerm_iot_central_application.iotc.subdomain
}

output "eventhub_namespace" {
  value = azurerm_eventhub_namespace.eh_ns.name
}

output "eventhub_name" {
  value = azurerm_eventhub.eh.name
}

output "stream_analytics_job" {
  value = azurerm_stream_analytics_job.sa.name
}

output "sql_server_name" {
  value = azurerm_sql_server.sqlserver.name
}

output "sql_database_name" {
  value = azurerm_sql_database.telemetry_db.name
}
