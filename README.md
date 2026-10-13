# TechCare Clinic

Arabic (RTL) clinic management web app with an AI assistant that answers from a local medical library, with source attribution.

## What it does

- Multi-clinic system: each clinic's data is isolated, and an owner (super admin) panel manages clinics, subscriptions and trial periods
- Roles and per-user permissions (admin, doctor, reception) with an audit log
- Patients, visits, appointments and queue, invoices, visit attachments, drug catalog
- Patient data rights and data retention handling
- AI assistant: retrieval over a medical library (PubMed and clinical guidelines) using a local LLM through LM Studio (Qwen), with an optional external model provider
- Optional vector search with PostgreSQL + pgvector

## Tech stack

- Backend: C# / .NET 10, ASP.NET Core, Entity Framework Core, SQLite (PostgreSQL optional)
- Frontend: React + Vite (Arabic, right-to-left)
- AI: LM Studio (local Qwen model and embeddings), pgvector for vector search
- Tests: integration tests that run the API against an in-memory SQLite database, with a fake LLM client (no network needed)

## Project structure

- `backend/TechCare.Clinic.Api`: REST API, domain, features, infrastructure, migrations
- `backend/TechCare.Clinic.Api.Tests`: integration tests
- `frontend`: web UI
- `scripts`: helper scripts for testing and starting the vector database
- `PLAN.md`, `PLAN_ADDENDUM.md`: development plan and step-by-step log

## Getting started

Requirements: .NET SDK 10, Node.js (frontend only).

1. Copy `backend/TechCare.Clinic.Api/appsettings.Development.example.json` to `appsettings.Development.json` and replace every `CHANGE-ME` with your own values (JWT key and seed passwords).
2. Run the API:

```powershell
dotnet run --project backend/TechCare.Clinic.Api/TechCare.Clinic.Api.csproj
```

3. Run the frontend:

```powershell
cd frontend; npm install; npm run dev
```

4. Run the tests:

```powershell
dotnet test backend/TechCare.Clinic.Api.Tests/TechCare.Clinic.Api.Tests.csproj
```

## Status

Working prototype under active development. This repository contains the source code only. The database, patient files, logs and the medical library (guideline PDFs and PubMed data) are not included.
