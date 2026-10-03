# Unit tests

Run from the repository root with Node.js and the .NET SDK installed:

- `npm test` or `npm run test:coverage`: unit tests plus coverage, failing below 80% lines or branches.
- `npm run test:unit`: tests without coverage.

The first run restores NuGet packages and the local ReportGenerator tool. No npm dependencies are needed for the C# runner.
All application source in the Gateway assembly is measured, including untested classes and startup code; tests and dependencies are excluded.
Generated reports are ignored by Git: `tests/coverage/coverage.cobertura.xml`, `tests/coverage/coverage.json`, and `tests/coverage/html/index.html`.
HTML is generated even when the 80% threshold fails. Missing/empty coverage reports are failures.
Tests use mocks, simulated HTTP responses and in-memory host configuration checks; no Docker, live Redis/RabbitMQ/Supabase or credentials are required.
Existing application/end-to-end tests are separate from this unit-test command.

Unit-test source and its project are centralized in `tests/unit/`. Production builds exclude `tests/**`.
The existing suite was migrated intact before extending it. Keep Contracts checked out beside this repository for its project reference.
