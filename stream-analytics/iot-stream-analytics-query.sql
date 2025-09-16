-- Input alias for IoT Hub (raw events)
WITH IoTInput AS (
    SELECT *
    FROM [event_hub_alias]
),

-- Telemetry with anomaly detection
TelemetryWithAnoms AS (
    SELECT
        deviceId,
        CAST(enqueuedTime AS datetime) AS enqueuedTime,
        telemetry.battery AS battery,
        telemetry.barometer AS barometer,
        
        telemetry.geolocation.lat AS latitude,
        telemetry.geolocation.lon AS longitude,
        telemetry.geolocation.alt AS altitude,
        
        -- Magnitudes
        SQRT(telemetry.accelerometer.x * telemetry.accelerometer.x +
             telemetry.accelerometer.y * telemetry.accelerometer.y +
             telemetry.accelerometer.z * telemetry.accelerometer.z) AS AccelMagnitude,
        
        SQRT(telemetry.gyroscope.x * telemetry.gyroscope.x +
             telemetry.gyroscope.y * telemetry.gyroscope.y +
             telemetry.gyroscope.z * telemetry.gyroscope.z) AS GyroMagnitude,
        
        SQRT(telemetry.magnetometer.x * telemetry.magnetometer.x +
             telemetry.magnetometer.y * telemetry.magnetometer.y +
             telemetry.magnetometer.z * telemetry.magnetometer.z) AS MagMagnitude,
        
        -- Anomaly detection (4 params: value, confidence, historySize, mode)
        AnomalyDetection_SpikeAndDip(CAST(telemetry.battery AS bigint), 95, 85, 'spikesanddips')
             OVER (LIMIT DURATION(second, 60)) AS BatteryAnom,
        
        AnomalyDetection_SpikeAndDip(telemetry.barometer, 95, 85, 'spikesanddips')
             OVER (LIMIT DURATION(second, 60)) AS BarometerAnom,
        
        AnomalyDetection_SpikeAndDip(
            SQRT(telemetry.accelerometer.x * telemetry.accelerometer.x +
                 telemetry.accelerometer.y * telemetry.accelerometer.y +
                 telemetry.accelerometer.z * telemetry.accelerometer.z),
            95, 85, 'spikesanddips'
        ) OVER (LIMIT DURATION(second, 60)) AS AccelAnom
    FROM IoTInput
)

-- 1️⃣ Raw passthrough into Bronze Data Lake
SELECT * INTO [bronze_datalake_ADLSGEN2_alias]
FROM IoTInput;

-- 2️⃣ Device metadata projection into Devices table
SELECT
    deviceId,
    applicationId,
    templateId,
    component,
    module
INTO [devices_sql_table_alias]
FROM IoTInput
WHERE deviceId IS NOT NULL;

-- 3️⃣ Telemetry with anomalies into Telemetry table
SELECT
    deviceId,
    enqueuedTime,
    battery,
    barometer,
    latitude,
    longitude,
    altitude,
    AccelMagnitude,
    GyroMagnitude,
    MagMagnitude,
    CASE
        WHEN BatteryAnom.IsAnomaly = 1 THEN 1
        WHEN BarometerAnom.IsAnomaly = 1 THEN 1
        WHEN AccelAnom.IsAnomaly = 1 THEN 1
        ELSE 0
    END AS Anomaly
INTO [telemetry_sql_table_alias]
FROM TelemetryWithAnoms
WHERE deviceId IS NOT NULL;