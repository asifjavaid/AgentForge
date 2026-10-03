# API Specification

The sample application exposes JSON APIs under `/api`. `POST /api/requirements/analyze` returns structured requirement analysis. `POST /api/repositories/analyze` runs the read-only repository agent. `POST /api/knowledge/ask` answers questions from the controlled documentation corpus.

Clients should send a correlation ID in the `X-Correlation-ID` header. The service returns RFC 9457 problem details for errors. Request bodies larger than 1 MiB are rejected at the gateway.

The health endpoint is `/health`. It does not require authentication and reveals no dependency credentials.
