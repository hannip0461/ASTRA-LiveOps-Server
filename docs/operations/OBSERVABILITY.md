# Observability Runtime

## Pipeline

```text
Astra.Api / Astra.TcpGateway / Astra.Silo / Astra.Worker
  -> OTLP/gRPC :4317
  -> EDOT Collector gateway
  -> OTel-native Elasticsearch data streams
  -> Kibana dashboard and alert rules
```

The self-managed path uses Elastic's EDOT Collector rather than sending SDK data directly to a legacy APM Server endpoint. The `elasticapm` processor and connector enrich traces and derive the metrics required by Elastic's APM views. The Elasticsearch exporter preserves OTel field names with `mapping.mode: otel`.

Application Counter instruments use delta temporality. Alert windows therefore evaluate new failures in the window instead of repeatedly matching a process-lifetime cumulative value.

## Local Resource Boundary

The entire stack is profile-gated and never starts with the PostgreSQL core profile.

- Elastic Stack components are pinned to `9.4.2`.
- Elasticsearch telemetry data uses a `1 GiB` `tmpfs`; it never writes to an unbounded named volume.
- Kibana's local data directory uses a separate `128 MiB` `tmpfs`.
- Container memory limits are 1280 MiB for Elasticsearch, 1024 MiB for Kibana, and 384 MiB for EDOT.
- Every container log uses 10 MiB files with three-file rotation.
- Data stream retention defaults to one hour, is capped at two hours, and rolls over after 15 minutes or 64 MiB.
- The three pinned amd64 images total about 1.89 GiB compressed. Images are fixed-cost layers; runtime telemetry disappears when the containers are removed.

This is a local portfolio profile. Security is disabled on loopback-only Elasticsearch/Kibana ports. Production requires TLS, API-key authentication, durable capacity planning, and an external retention policy.

## Run And Verify

```powershell
./scripts/observability/Start-Observability.ps1
./scripts/observability/Test-Observability.ps1
./scripts/observability/Test-OperationalScenarios.ps1
```

The test is end to end, not a container health check. It:

1. emits a real OTLP trace and metric from `Astra.ObservabilityProbe`;
2. verifies both OTel-native data streams through ES|QL;
3. applies bounded lifecycle settings;
4. provisions the ASTRA dashboard through Kibana's Dashboards API;
5. provisions the PostgreSQL pool-timeout rule with a stable rule ID;
6. emits one explicitly tagged synthetic timeout metric;
7. waits until Kibana reports an active alert and verifies the dashboard by ID.

`Test-OperationalScenarios.ps1` then runs two real fault scenarios:

1. a two-slot PostgreSQL pool is exhausted, one acquisition times out within its budget, and a query succeeds after the held connections are released;
2. one valid and one invalid Outbox event are consumed by the running Worker, proving `published`, bounded retry, `dead_letter`, and alert activation.

The script calculates the 99.9% acquisition-SLO burn rate from delta `attempts` and `failures`. It writes the isolated run result to `output/evidence/operational-scenarios.json`; the probe ID is also the OTel `service.instance.id`, so prior telemetry cannot inflate the evidence.

Application services export to the same gateway when started with:

```powershell
$env:Astra__OpenTelemetry__OtlpEndpoint='http://127.0.0.1:4317'
```

Kibana is available at `http://127.0.0.1:5609`. The dashboard URL is printed by both test scripts.

## Stop Without Growth

```powershell
./scripts/observability/Stop-Observability.ps1
```

The stop script removes only the three observability containers and their ephemeral mounts. It leaves PostgreSQL, its named volume, and the pinned image layers untouched. No prune command is executed implicitly.
