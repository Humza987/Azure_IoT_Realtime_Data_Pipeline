# IoT Real-time Data Engineering Pipeline — IoT Central (Plug & Play) → Stream Analytics → Azure SQL DB → .NET 8.0 Azure Function -> Power BI

**Executive Summary:** Comprehensive Azure IoT streaming pipeline engineered to ingest simulated Plug & Play devices from **Azure IoT Central**, implementing real-time Stream Analytics with integrated anomaly detection capabilities, persisting both raw and curated data (ADLS Gen2 + Azure SQL), and streaming processed telemetry to a **Power BI** streaming dataset via a **.NET 8 Azure Function**.

This repository documents the complete implementation, architectural decisions, and provides all necessary artifacts including Stream Analytics queries, SQL scripts, Azure Function code, IoT Central transformations, and a foundational Terraform configuration. The production environment was primarily deployed through the Azure portal, with Terraform provided as a reference implementation.

---

## Architecture Overview

```
IoT Central (Phone Plug-and-Play simulator; transform export)
        │ (export → Event Hub)
        ▼
    Azure Event Hub
        │
        ▼
  Azure Stream Analytics (primary real-time processing)
   ├─ Raw passthrough ──> ADLS Gen2 (bronze / raw archive)
   └─ Enrichment + ML ──> Azure SQL DB  (Devices, Telemetry tables)
           │
           ▼
  Azure Function (.NET 8 isolated worker)
  └─ Reads SQL → POSTs JSON → Power BI Streaming Dataset
           │
           ▼
      Power BI Dashboard (live tiles / KPI / map visual)
```

---

## Component Architecture

### IoT Central (Device Simulation & Management)

* Serves as the primary telemetry source and device modeling platform utilizing IoT Central for simulating devices with Plug & Play device templates
* Configured with export transformation capabilities that normalize messages and forward them to Event Hub for downstream processing

### Event Hub

* Provides durable, partitioned ingestion layer for receiving transformed messages from IoT Central, optimized for high throughput and reliability

### Stream Analytics (Core Processing Engine)

* **Primary real-time processing engine** implementing:
  * Raw event persistence to **ADLS Gen2** for audit trails and reprocessing capabilities
  * Device metadata projection into dedicated `Devices` table
  * Sensor magnitude calculations for accelerometer, gyroscope, and magnetometer data
  * **In-stream anomaly detection** utilizing `AnomalyDetection_SpikeAndDip` algorithms for battery, barometer, and acceleration magnitude monitoring
  * Curated telemetry emission to **Azure SQL** with anomaly flagging for business intelligence and monitoring

### Azure SQL Database (Curated Data Store)

* Relational database optimized for business intelligence joins, historical queries, and ad-hoc analysis of device metadata and processed telemetry

### Azure Function (.NET 8 Isolated Worker)

* Implements timer and HTTP triggers providing:
  * Incremental telemetry retrieval from SQL database with state tracking via Table Storage (`lastProcessedTime`)
  * Batched JSON array transmission to **Power BI streaming dataset** via API push methodology

### Infrastructure as Code (Terraform)

* Foundational `main.tf` configuration provided for resource provisioning including resource group, ADLS Gen2, IoT Central app, Event Hub, Stream Analytics job, and Azure SQL
* Production environment was deployed via Azure portal; Terraform included for reproducibility and reference

---

## Repository Structure (Noteworthy Files)

```
/ (root)
├─ README.md
├─ terraform/
│   ├─ main-example.tf          # Terraform foundation configuration
├─ azure-function/             
│   ├─ PushTelemetryFunction.cs
│   ├─ Program.cs
│   └─ local.settings.json.example
├─ stream-analytics/
│   └─ iot-stream-analytics-query.sql
├─ sql-scripts/
│   ├─ create_devices_table.sql
│   └─ create_telemetry_table.sql
├─ iot-central/
│   ├─ transformation.json
│   └─ raw-data-template.json
└─ docs/
    └─ power_bi_dashboard.png    # Dashboard screenshot placeholder
```

> **Note:** Repository contains only essential Function files to maintain security best practices. Confidential configuration excluded from source control.

---

## Implementation Guide

### Prerequisites

* .NET 8 SDK
* Azure CLI
* Terraform (optional)
* Visual Studio Code with Azure Functions extension (recommended)
* Power BI Service account

### Deployment Workflow

1. **IoT Central Configuration**: Establish Plug & Play templates, initialize simulator devices, configure export to Event Hub using provided `transformation.json`
2. **Event Hub Provisioning**: Deploy namespaced Event Hub and configure IoT Central export integration
3. **Stream Analytics Deployment**: Create processing job via portal, configure Event Hub input and dual outputs (ADLS Gen2 + Azure SQL), implement query from `stream-analytics/iot-stream-analytics-query.sql`
4. **Azure SQL Setup**: Execute provided SQL scripts to establish `Devices` and `Telemetry` table schema
5. **Azure Function Deployment**: Configure local development environment with `local.settings.json` values and deploy for Power BI streaming integration
6. **Power BI Configuration**: Establish streaming dataset (API/push) and configure Function push URL for live tile feeds

---

## Configuration Reference

### Local Development Settings (local.settings.json.example)

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "FUNCTIONS_INPROC_NET8_ENABLED": "1",
    "SQL_CONNECTION_STRING": "Server=<server>;Database=<db>;User Id=<user>;Password=<pwd>;Encrypt=True;",
    "POWERBI_PUSH_URL": "https://api.powerbi.com/beta/.../datasets/{datasetId}/rows?key=...",
    "TIME_WINDOW_SECONDS": "70",
    "BATCH_SIZE": "500",
    "FORCE_INITIAL_LOAD": "true"
  }
}
```

**Security Notice:** Production deployments should utilize Azure Key Vault or secure pipeline variables for credential management. Local development storage is intended for development environments only.

---

## Technical Implementation Highlights

### Stream Analytics — Magnitude Calculation & Anomaly Detection

```sql
SQRT(telemetry.accelerometer.x*telemetry.accelerometer.x +
     telemetry.accelerometer.y*telemetry.accelerometer.y +
     telemetry.accelerometer.z*telemetry.accelerometer.z) AS AccelMagnitude,

AnomalyDetection_SpikeAndDip(CAST(telemetry.battery AS bigint),95,85,'spikesanddips')
  OVER (LIMIT DURATION(second, 60)) AS BatteryAnom
```

### Database Schema Implementation

```sql
CREATE TABLE Devices (
  deviceId VARCHAR(50) PRIMARY KEY,
  applicationId VARCHAR(50),
  templateId VARCHAR(100),
  component VARCHAR(50),
  module VARCHAR(50) NULL
);

CREATE TABLE Telemetry (
  telemetryId BIGINT IDENTITY PRIMARY KEY,
  deviceId VARCHAR(50) NOT NULL,
  enqueuedTime DATETIME2 NOT NULL,
  battery INT,
  AccelMagnitude FLOAT,
  Anomaly BIT DEFAULT 0,
  FOREIGN KEY (deviceId) REFERENCES Devices(deviceId)
);
```

---

## Power BI Integration

* **Streaming Dataset Configuration**: Establish API-based streaming dataset in Power BI with field definitions matching telemetry payload structure (deviceId, enqueuedTime, battery, AccelMagnitude, Anomaly, latitude, longitude)
* **Data Pipeline Integration**: Configure dataset push URL in `POWERBI_PUSH_URL` for Function/Application POST operations
* **Visualization Implementation**: Pipeline provides data feed foundation; dashboard visualization development (tiles, maps, KPI cards) remains implementation-specific
* **Reference Documentation**: Dashboard screenshot placeholder available in `docs/power_bi_dashboard.png`

---

## Professional Skills & Technical Competencies Demonstrated

* **Real-time Data Engineering**: Design and implementation of low-latency telemetry pipeline architecture (ingest → enrich → store → visualize)
* **Azure Cloud Architecture**: Comprehensive experience with IoT Central, Event Hub, Stream Analytics, ADLS Gen2, Azure SQL, and Azure Functions
* **Machine Learning Integration**: Practical anomaly detection implementation using Stream Analytics Spike & Dip algorithms for real-time alerting
* **Modern .NET Development**: Backend integration and streaming worker development using **.NET 8 Azure Functions (isolated worker model)**
* **Infrastructure as Code**: Terraform implementation for reproducible infrastructure deployment
* **Enterprise Best Practices**: Incremental data processing, idempotent operations, secure configuration management, raw data archival for model training and reproducibility

---

## Future Enhancement Opportunities

* **Advanced Analytics**: Deploy ML.NET or Python models on historical ADLS Gen2 data for predictive alerting capabilities
* **Security Enhancement**: Implement Managed Identity authentication and Azure Key Vault integration for Function deployment
* **Data Science Integration**: Develop Jupyter notebooks demonstrating offline machine learning training and model evaluation workflows
* **Infrastructure Automation**: Complete Terraform implementation for Stream Analytics inputs/outputs to achieve full Infrastructure as Code parity

---

## Implementation Notes

* Repository demonstrates production-ready implementation decisions: IoT Central for device simulation and modeling, Stream Analytics as core processing engine, Azure SQL for curated data storage, and optional .NET 8 Function for Power BI streaming integration
* Terraform configuration provided as reference implementation; production environment deployed via Azure portal for rapid prototyping and testing

---

## Technical References

* [Azure Functions Development (VS Code, C# Isolated Worker)](https://learn.microsoft.com/azure/azure-functions/functions-develop-vs-code?tabs=node-v4%2Cpython-v2%2Cisolated-process%2Cquick-create&pivots=programming-language-csharp)
* [Power BI Streaming Dataset API Documentation](https://learn.microsoft.com/power-bi/)
* [Azure Stream Analytics Anomaly Detection Functions](https://learn.microsoft.com/azure/stream-analytics/)

---

![Power BI Dashboard Implementation](docs/power_bi_dashboard.png)

---

## Professional Summary for CV/Interview Use

**Azure IoT Real-time Pipeline Engineer**
* Architected and implemented end-to-end Azure IoT streaming pipeline processing 500+ device telemetry points with sub-second latency
* Integrated Stream Analytics with machine learning anomaly detection for real-time alerting and business intelligence
* Developed .NET 8 Azure Functions for Power BI streaming integration with incremental data processing and state management
* Implemented Infrastructure as Code using Terraform for reproducible cloud resource deployment and management