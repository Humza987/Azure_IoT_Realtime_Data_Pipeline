using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Data;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text;
using Azure.Data.Tables;
using Azure;

namespace TelemetryToPowerBI
{
    public class TelemetryFunction
    {
        private static readonly HttpClient httpClient = new HttpClient();

        // Timer-triggered function - runs every 10 seconds
        [Function("ContinuousTelemetrySync")]
        public async Task RunContinuousSync(
            [TimerTrigger("*/10 * * * * *")] TimerInfo myTimer,
            FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("ContinuousTelemetrySync");
            
            if (myTimer.ScheduleStatus is not null)
            {
                logger.LogInformation($"Timer trigger function executed at: {DateTime.Now}");
                logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");
            }

            await ProcessNewTelemetryData(logger, isManualTrigger: false);
        }

        // Manual trigger function - for initial loads or manual sync
        [Function("InitialDataLoad")]
        public async Task<HttpResponseData> RunManualSync(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("InitialDataLoad");
            logger.LogInformation("Manual sync triggered");

            // Check if this is a full initial load or incremental sync
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            bool isInitialLoad = false;
            
            if (!string.IsNullOrEmpty(requestBody))
            {
                try
                {
                    var json = JObject.Parse(requestBody);
                    isInitialLoad = json["initialLoad"]?.Value<bool>() ?? false;
                }
                catch
                {
                    // If JSON parsing fails, default to incremental
                }
            }

            try
            {
                if (isInitialLoad)
                {
                    await ProcessInitialLoad(logger);
                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteStringAsync("Initial load completed successfully");
                    return response;
                }
                else
                {
                    int recordsProcessed = await ProcessNewTelemetryData(logger, isManualTrigger: true);
                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteStringAsync($"Manual sync completed. Processed {recordsProcessed} new records.");
                    return response;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during manual sync");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync($"Error: {ex.Message}");
                return response;
            }
        }

        private static async Task<int> ProcessNewTelemetryData(ILogger logger, bool isManualTrigger = false)
        {
            var powerBiUrl = Environment.GetEnvironmentVariable("POWERBI_PUSH_URL");
            var connStr = GetConnectionString();

            if (string.IsNullOrEmpty(powerBiUrl) || string.IsNullOrEmpty(connStr))
            {
                logger.LogWarning("Missing configuration - skipping sync");
                return 0;
            }

            try
            {
                // Get the last processed timestamp
                DateTime lastProcessedTime = await GetLastProcessedTimestamp();
                
                using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                // Get only new records since last processed time
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT deviceId, enqueuedTime, battery, barometer, latitude, longitude, altitude,
                           AccelMagnitude, GyroMagnitude, MagMagnitude, Anomaly
                    FROM Telemetry
                    WHERE enqueuedTime > @lastProcessedTime
                    ORDER BY enqueuedTime ASC;
                ";
                cmd.Parameters.Add(new SqlParameter("@lastProcessedTime", SqlDbType.DateTime2) { Value = lastProcessedTime });

                var payloadArray = new JArray();
                DateTime? newLastProcessedTime = null;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var obj = BuildTelemetryObject(reader);
                    payloadArray.Add(obj);
                    
                    // Track the latest timestamp
                    if (reader["enqueuedTime"] != DBNull.Value && reader["enqueuedTime"] is DateTime enqueuedTime)
                    {
                        if (newLastProcessedTime == null || enqueuedTime > newLastProcessedTime)
                        {
                            newLastProcessedTime = enqueuedTime;
                        }
                    }
                }

                if (payloadArray.Count > 0)
                {
                    // Send to Power BI
                    bool success = await SendToPowerBI(payloadArray, powerBiUrl, logger);
                    
                    if (success && newLastProcessedTime.HasValue)
                    {
                        // Update the last processed timestamp
                        await UpdateLastProcessedTimestamp(newLastProcessedTime.Value);
                        
                        if (isManualTrigger)
                        {
                            logger.LogInformation($"Manual sync: Successfully pushed {payloadArray.Count} new records to Power BI");
                        }
                        else
                        {
                            logger.LogInformation($"Continuous sync: Pushed {payloadArray.Count} new records to Power BI");
                        }
                        
                        return payloadArray.Count;
                    }
                    else if (!success)
                    {
                        logger.LogError($"Failed to push {payloadArray.Count} records to Power BI");
                        return 0;
                    }
                }
                else
                {
                    if (isManualTrigger)
                    {
                        logger.LogInformation("Manual sync: No new records found");
                    }
                    // Don't log for timer trigger when no new records - reduces noise
                }

                return payloadArray.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during telemetry data processing");
                return 0;
            }
        }

        private static async Task ProcessInitialLoad(ILogger logger)
        {
            var powerBiUrl = Environment.GetEnvironmentVariable("POWERBI_PUSH_URL");
            var connStr = GetConnectionString();

            if (string.IsNullOrEmpty(powerBiUrl) || string.IsNullOrEmpty(connStr))
            {
                throw new InvalidOperationException("Missing configuration");
            }

            int batchSize = 500;
            var batchSizeStr = Environment.GetEnvironmentVariable("BATCH_SIZE");
            if (!string.IsNullOrEmpty(batchSizeStr) && int.TryParse(batchSizeStr, out var parsed))
                batchSize = parsed;

            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // Get total count
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Telemetry";
            var totalRecords = (int)await countCmd.ExecuteScalarAsync();
            logger.LogInformation($"Starting initial bulk load of {totalRecords} records in batches of {batchSize}");

            if (totalRecords == 0)
            {
                logger.LogInformation("No data found in database");
                return;
            }

            int totalPushed = 0;
            int offset = 0;
            var startTime = DateTime.UtcNow;
            DateTime? latestTimestamp = null;

            while (offset < totalRecords)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT deviceId, enqueuedTime, battery, barometer, latitude, longitude, altitude,
                           AccelMagnitude, GyroMagnitude, MagMagnitude, Anomaly
                    FROM Telemetry
                    ORDER BY enqueuedTime ASC
                    OFFSET @offset ROWS
                    FETCH NEXT @batchSize ROWS ONLY;
                ";
                cmd.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = offset });
                cmd.Parameters.Add(new SqlParameter("@batchSize", SqlDbType.Int) { Value = batchSize });

                var payloadArray = new JArray();
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var obj = BuildTelemetryObject(reader);
                    payloadArray.Add(obj);
                    
                    // Track the latest timestamp for initial sync
                    if (reader["enqueuedTime"] != DBNull.Value && reader["enqueuedTime"] is DateTime enqueuedTime)
                    {
                        if (latestTimestamp == null || enqueuedTime > latestTimestamp)
                        {
                            latestTimestamp = enqueuedTime;
                        }
                    }
                }

                if (payloadArray.Count > 0)
                {
                    bool success = await SendToPowerBI(payloadArray, powerBiUrl, logger);
                    if (success)
                    {
                        totalPushed += payloadArray.Count;
                        var elapsed = DateTime.UtcNow - startTime;
                        logger.LogInformation($"Progress: {totalPushed}/{totalRecords} ({totalPushed * 100.0 / totalRecords:F1}%) - Elapsed: {elapsed.TotalMinutes:F1} min");
                    }
                    else
                    {
                        throw new Exception($"Failed to push batch at offset {offset}");
                    }

                    // Delay between batches to avoid overwhelming Power BI
                    await Task.Delay(200);
                }

                offset += batchSize;
            }

            // Update the last processed timestamp after successful initial load
            if (latestTimestamp.HasValue)
            {
                await UpdateLastProcessedTimestamp(latestTimestamp.Value);
            }

            var duration = DateTime.UtcNow - startTime;
            logger.LogInformation($"Initial load completed! Successfully pushed {totalPushed}/{totalRecords} records in {duration.TotalMinutes:F1} minutes");
        }

        private static async Task<DateTime> GetLastProcessedTimestamp()
        {
            try
            {
                var connStr = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
                if (string.IsNullOrEmpty(connStr))
                {
                    // If no storage account configured, start from 1 hour ago
                    return DateTime.UtcNow.AddHours(-1);
                }

                var tableClient = new TableClient(connStr, "TelemetrySync");
                await tableClient.CreateIfNotExistsAsync();

                var entity = await tableClient.GetEntityIfExistsAsync<TableEntity>("sync", "lastProcessed");
                if (entity.HasValue && entity.Value.ContainsKey("LastProcessedTime"))
                {
                    return entity.Value.GetDateTime("LastProcessedTime") ?? DateTime.UtcNow.AddHours(-1);
                }

                // Default to 1 hour ago if no record exists
                return DateTime.UtcNow.AddHours(-1);
            }
            catch (Exception)
            {
                // If table storage fails, default to 1 hour ago
                return DateTime.UtcNow.AddHours(-1);
            }
        }

        private static async Task UpdateLastProcessedTimestamp(DateTime timestamp)
        {
            try
            {
                var connStr = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
                if (string.IsNullOrEmpty(connStr))
                {
                    return; // Skip if no storage configured
                }

                var tableClient = new TableClient(connStr, "TelemetrySync");
                await tableClient.CreateIfNotExistsAsync();

                var entity = new TableEntity("sync", "lastProcessed")
                {
                    ["LastProcessedTime"] = timestamp
                };

                await tableClient.UpsertEntityAsync(entity);
            }
            catch (Exception)
            {
                // If table storage fails, continue - next run will use default lookback
            }
        }

        private static JObject BuildTelemetryObject(SqlDataReader reader)
        {
            var obj = new JObject();

            // deviceId (text)
            obj["deviceId"] = reader["deviceId"] != DBNull.Value ?
                JToken.FromObject(reader["deviceId"].ToString() ?? string.Empty) :
                JValue.CreateString(string.Empty);

            // enqueuedTime as "yyyy-MM-ddTHH:mm:ss.fffZ"
            if (reader["enqueuedTime"] != DBNull.Value && reader.GetFieldType(reader.GetOrdinal("enqueuedTime")) == typeof(DateTime))
            {
                DateTime enq = ((DateTime)reader["enqueuedTime"]).ToUniversalTime();
                obj["enqueuedTime"] = enq.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            }
            else
            {
                obj["enqueuedTime"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            }

            // Numeric fields
            void AddNumericField(string colName)
            {
                var val = reader[colName];
                if (val == DBNull.Value)
                {
                    obj[colName] = 0.0;
                    return;
                }

                var s = val.ToString() ?? string.Empty;
                if (double.TryParse(s, out var d))
                {
                    obj[colName] = JToken.FromObject(d);
                }
                else
                {
                    obj[colName] = 0.0;
                }
            }

            AddNumericField("battery");
            AddNumericField("barometer");
            AddNumericField("latitude");
            AddNumericField("longitude");
            AddNumericField("altitude");
            AddNumericField("AccelMagnitude");
            AddNumericField("GyroMagnitude");
            AddNumericField("MagMagnitude");

            // Anomaly
            var anomalyVal = reader["Anomaly"];
            if (anomalyVal == DBNull.Value)
            {
                obj["Anomaly"] = 0.0;
            }
            else
            {
                if (anomalyVal is bool b) obj["Anomaly"] = b ? 1.0 : 0.0;
                else if (double.TryParse(anomalyVal.ToString(), out var an)) obj["Anomaly"] = an;
                else obj["Anomaly"] = 0.0;
            }

            return obj;
        }

        private static async Task<bool> SendToPowerBI(JArray payloadArray, string powerBiUrl, ILogger logger)
        {
            try
            {
                var payloadJson = payloadArray.ToString(Formatting.None);
                var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                var resp = await httpClient.PostAsync(powerBiUrl, content);
                var respText = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogError($"Power BI error - Status: {resp.StatusCode}, Body: {respText}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Exception sending to Power BI");
                return false;
            }
        }

        private static string GetConnectionString()
        {
            var full = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
            if (!string.IsNullOrEmpty(full)) return full;

            var server = Environment.GetEnvironmentVariable("SQL_SERVER");
            var database = Environment.GetEnvironmentVariable("SQL_DATABASE");
            var user = Environment.GetEnvironmentVariable("SQL_USER");
            var pwd = Environment.GetEnvironmentVariable("SQL_PASSWORD");

            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(database) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pwd))
                return string.Empty;

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                UserID = user,
                Password = pwd,
                Encrypt = true,
                TrustServerCertificate = false,
                ConnectTimeout = 30
            };
            return builder.ToString();
        }
    }
}