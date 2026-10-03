# Deployment

AgentForge is deployed as a Linux container to Azure Container Apps in the West Europe region. Production uses two minimum replicas and scales to ten replicas when concurrent HTTP requests exceed 50 per replica.

Configuration is supplied through environment variables. The container receives a workload identity. Application Insights receives structured telemetry, while secrets and full prompt content must not be logged.

The deployment pipeline builds and tests the complete solution before publishing an immutable container image.
