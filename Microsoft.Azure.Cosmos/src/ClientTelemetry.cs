﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using HdrHistogram;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.CosmosElements.Telemetry;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Documents;
    using Newtonsoft.Json.Linq;

    internal class ClientTelemetry
    {
        internal const String RequestKey = "telemetry";

        internal const int OneKbToBytes = 1024;

        internal const int RequestLatencyMaxMicroSec = 300000000;
        internal const int RequestLatencySuccessPrecision = 4;
        internal const int RequestLatencyFailurePrecision = 2;
        internal const string RequestLatencyName = "RequestLatency";
        internal const string RequestLatencyUnit = "MicroSec";

        internal const int RequestChargeMax = 10000;
        internal const int RequestChargePrecision = 2;
        internal const string RequestChargeName = "RequestCharge";
        internal const string RequestChargeUnit = "RU";

        internal const string VMMetadataURL = "http://169.254.169.254/metadata/instance?api-version=2020-06-01";

        internal const double Percentile50 = 50.0;
        internal const double Percentile90 = 90.0;
        internal const double Percentile95 = 95.0;
        internal const double Percentile99 = 99.0;
        internal const double Percentile999 = 99.9;

        public const string EnvPropsClientTelemetrySchedulingInSeconds = "COSMOS.CLIENT_TELEMETRY_SCHEDULING_IN_SECONDS";
        public const string EnvPropsClientTelemetryEnabled = "COSMOS.CLIENT_TELEMETRY_ENABLED";
        internal const string DefaultTimeStampInSeconds = "600";

        internal readonly ClientTelemetryInfo ClientTelemetryInfo;
        internal readonly CosmosHttpClient HttpClient;
        internal readonly bool IsClientTelemetryEnabled;
        internal readonly double ClientTelemetrySchedulingInSeconds;

        public ClientTelemetry(bool? acceleratedNetworking,
                               string clientId,
                               string processId,
                               string userAgent,
                               ConnectionMode connectionMode,
                               string globalDatabaseAccountName,
                               CosmosHttpClient httpClient,
                               bool isClientTelemetryEnabled)
        {
            this.ClientTelemetryInfo = new ClientTelemetryInfo(clientId, processId, userAgent, connectionMode,
                globalDatabaseAccountName, acceleratedNetworking);

            this.HttpClient = httpClient;
            this.IsClientTelemetryEnabled = ConfigurationManager
                .GetEnvironmentVariable<bool>(EnvPropsClientTelemetryEnabled, isClientTelemetryEnabled);
            this.ClientTelemetrySchedulingInSeconds = ConfigurationManager
                .GetEnvironmentVariable<double>(EnvPropsClientTelemetrySchedulingInSeconds, DefaultTimeStampInSeconds);
        }

        internal async Task<AzureVMMetadata> LoadAzureVmMetaDataAsync()
        {
            AzureVMMetadata azMetadata = null;
            try
            {
                static ValueTask<HttpRequestMessage> CreateRequestMessage()
                {
                    HttpRequestMessage request = new HttpRequestMessage
                    {
                        Method = HttpMethod.Get,
                        RequestUri = new Uri(VMMetadataURL)
                    };
                    request.Headers.Add("Metadata", "true");

                    return new ValueTask<HttpRequestMessage>(request);
                }
                using HttpResponseMessage httpResponseMessage = await this.HttpClient.SendHttpAsync(
                    createRequestMessageAsync: CreateRequestMessage,
                    resourceType: ResourceType.Unknown,
                    timeoutPolicy: HttpTimeoutPolicyControlPlaneRead.Instance,
                    trace: NoOpTrace.Singleton,
                    cancellationToken: default);
                azMetadata = await ProcessResponseAsync(httpResponseMessage);

                this.ClientTelemetryInfo.ApplicationRegion = azMetadata.Location;
                this.ClientTelemetryInfo.HostEnvInfo = String.Concat(azMetadata.OSType, "|", azMetadata.SKU,
                    "|", azMetadata.VMSize, "|", azMetadata.AzEnvironment);
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to get Azure VM info:" + e.ToString());
            }

            return azMetadata;
        }

        internal static async Task<AzureVMMetadata> ProcessResponseAsync(HttpResponseMessage httpResponseMessage)
        {
            string jsonVmInfo = await httpResponseMessage.Content.ReadAsStringAsync();
            return JObject.Parse(jsonVmInfo).ToObject<AzureVMMetadata>();
        }

        internal void Collect(CosmosDiagnostics cosmosDiagnostics,
                            HttpStatusCode statusCode,
                            int objectSize,
                            string containerId,
                            string databaseId,
                            OperationType operationType,
                            ResourceType resourceType,
                            ConsistencyLevel? consistencyLevel,
                            double requestCharge)
        {
            ReportPayload reportPayloadLatency =
                this.CreateReportPayload(cosmosDiagnostics, statusCode, objectSize, containerId, databaseId, operationType,
                    resourceType, consistencyLevel, RequestLatencyName, RequestLatencyUnit);

            this.ClientTelemetryInfo
                .OperationInfoMap
                .TryGetValue(reportPayloadLatency, out LongConcurrentHistogram latencyHistogram);

            if (latencyHistogram == null)
            {
                latencyHistogram = statusCode.IsSuccess()
                    ? new LongConcurrentHistogram(1, RequestLatencyMaxMicroSec, RequestLatencySuccessPrecision)
                    : new LongConcurrentHistogram(1, RequestLatencyMaxMicroSec, RequestLatencyFailurePrecision);
            }
            latencyHistogram.RecordValue((long)cosmosDiagnostics.GetClientElapsedTime().TotalMilliseconds * 1000);
            this.ClientTelemetryInfo.OperationInfoMap[reportPayloadLatency] = latencyHistogram;

            ReportPayload reportPayloadRequestCharge =
               this.CreateReportPayload(cosmosDiagnostics, statusCode, objectSize, containerId, databaseId, operationType,
                   resourceType, consistencyLevel, RequestChargeName, RequestChargeUnit);

            this.ClientTelemetryInfo
                .OperationInfoMap
                .TryGetValue(reportPayloadLatency, out LongConcurrentHistogram requestChargeHistogram);

            if (requestChargeHistogram == null)
            {
                requestChargeHistogram = new LongConcurrentHistogram(1, RequestChargeMax, RequestChargePrecision);
            }
            requestChargeHistogram.RecordValue((long)requestCharge);
            this.ClientTelemetryInfo.OperationInfoMap[reportPayloadRequestCharge] = requestChargeHistogram;
        }

        internal ReportPayload CreateReportPayload(CosmosDiagnostics cosmosDiagnostics,
                                                  HttpStatusCode statusCode,
                                                  int objectSize,
                                                  string containerId,
                                                  string databaseId,
                                                  OperationType operationType,
                                                  ResourceType resourceType,
                                                  ConsistencyLevel? consistencyLevel,
                                                  string metricsName,
                                                  string unitName)
        {
            IReadOnlyList<(string regionName, Uri uri)> regionList = cosmosDiagnostics.GetContactedRegions();
            IList<Uri> regionUris = new List<Uri>();
            foreach ((_, Uri uri) in regionList)
                regionUris.Add(uri);

            ReportPayload reportPayload = new ReportPayload(metricsName, unitName)
            {
                RegionsContacted = string.Join(",", regionUris),
                Consistency = consistencyLevel.GetValueOrDefault(),
                DatabaseName = databaseId,
                ContainerName = containerId,
                Operation = operationType,
                Resource = resourceType,
                StatusCode = (int)statusCode
            };

            if (objectSize != 0)
            {
                reportPayload.GreaterThan1Kb = objectSize > OneKbToBytes;
            }

            return reportPayload;
        }

        internal async Task ReadAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(this.ClientTelemetrySchedulingInSeconds), default);

                if (this.IsClientTelemetryEnabled)
                {
                    DefaultTrace.TraceInformation("ReadAsync() - Reading Client Telemetry Information");
                    foreach (KeyValuePair<ReportPayload, LongConcurrentHistogram> entry in this.ClientTelemetryInfo.CacheRefreshInfoMap)
                    {
                        this.FillMetricsInfo(entry.Key, entry.Value);
                    }
                    foreach (KeyValuePair<ReportPayload, LongConcurrentHistogram> entry in this.ClientTelemetryInfo.OperationInfoMap)
                    {
                        this.FillMetricsInfo(entry.Key, entry.Value);
                    }
                } 
                else
                {
                    DefaultTrace.TraceInformation("ReadAsync() - Client Telemetry is disabled");
                }
                this.Reset();

                await this.ReadAsync();
            } 
            catch (Exception ex)
            {
                DefaultTrace.TraceCritical("ReadAsync() - Unable to read telemetry information. Exception: {0}", ex.ToString());
                this.Reset();
                await this.ReadAsync();
            }
        }

        private void FillMetricsInfo(ReportPayload payload, LongConcurrentHistogram histogram)
        {
            LongConcurrentHistogram copyHistogram = (LongConcurrentHistogram)histogram.Copy();
            payload.MetricInfo.Count = copyHistogram.TotalCount;
            payload.MetricInfo.Max = copyHistogram.GetMaxValue();
            //payload.MetricInfo.Min = copyHistogram.GetMinValue();
            payload.MetricInfo.Mean = copyHistogram.GetMean();
            IDictionary<Double, Double> percentile = new Dictionary<Double, Double>
            {
                { Percentile50,  copyHistogram.GetValueAtPercentile(Percentile50) },
                { Percentile90,  copyHistogram.GetValueAtPercentile(Percentile90) },
                { Percentile95,  copyHistogram.GetValueAtPercentile(Percentile95) },
                { Percentile99,  copyHistogram.GetValueAtPercentile(Percentile99) },
                { Percentile999, copyHistogram.GetValueAtPercentile(Percentile999) }
            };
            payload.MetricInfo.Percentiles = percentile;
        }

        internal async Task<AzureVMMetadata> InitAsync()
        {
            //if (this.isClientTelemetryEnabled)
            return await this.LoadAzureVmMetaDataAsync();
        }

        internal void Reset()
        {
            this.ClientTelemetryInfo.OperationInfoMap.Clear();
            this.ClientTelemetryInfo.SystemInfoMap.Clear();
            this.ClientTelemetryInfo.CacheRefreshInfoMap.Clear();

        }

    }
}
