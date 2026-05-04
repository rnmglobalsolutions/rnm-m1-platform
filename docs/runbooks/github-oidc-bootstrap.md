# GitHub OIDC Bootstrap

This runbook configures GitHub Actions to deploy RNM Platform M1 to Azure without storing Azure client secrets in GitHub.

## Prerequisites

- Azure CLI installed locally.
- Permission to create Entra applications or managed identities.
- Permission to assign roles on the target resource group.
- A GitHub repository with Actions enabled.

## 1. Create the resource groups

The deployment workflows assume the resource groups already exist. Use one Azure resource group per GitHub environment so dev, staging, and production each get their own Storage Account, Function App, Application Insights, and Key Vault.

```bash
az group create \
  --name <DEV_RESOURCE_GROUP_NAME> \
  --location eastus

az group create \
  --name <STAGING_RESOURCE_GROUP_NAME> \
  --location eastus

az group create \
  --name <PRODUCTION_RESOURCE_GROUP_NAME> \
  --location eastus
```

Use the same location as the GitHub repository variable `AZURE_LOCATION`.

## 2. Create the GitHub deployment identity

Use either an Entra app registration or a user-assigned managed identity. The workflow is written for an Entra app client id.

```bash
az ad app create --display-name "rnm-github-actions"
```

Capture the returned `appId`; this is the value for `AZURE_CLIENT_ID`.

Then create the service principal:

```bash
az ad sp create --id <APP_ID>
```

## 3. Add the federated credential

This links GitHub's OIDC token issuer to the Entra app. Replace the owner and repo placeholders with the exact GitHub repository path.

```bash
az ad app federated-credential create \
  --id <APP_ID> \
  --parameters '{
    "name": "github-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:<GITHUB_OWNER>/<REPOSITORY_NAME>:ref:refs/heads/main",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

Add more federated credentials only if another trusted branch or environment should deploy. Feature branches should not deploy.

## 4. Assign Azure permissions

Grant Contributor plus role-assignment permission at each environment resource group scope so the workflow can deploy Bicep resources and assign managed identity access to Key Vault and deployment storage.

```bash
az role assignment create \
  --assignee <APP_ID> \
  --role Contributor \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<DEV_RESOURCE_GROUP_NAME>

az role assignment create \
  --assignee <APP_ID> \
  --role Contributor \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<STAGING_RESOURCE_GROUP_NAME>

az role assignment create \
  --assignee <APP_ID> \
  --role Contributor \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<PRODUCTION_RESOURCE_GROUP_NAME>

az role assignment create \
  --assignee <APP_ID> \
  --role "User Access Administrator" \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<DEV_RESOURCE_GROUP_NAME>

az role assignment create \
  --assignee <APP_ID> \
  --role "User Access Administrator" \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<STAGING_RESOURCE_GROUP_NAME>

az role assignment create \
  --assignee <APP_ID> \
  --role "User Access Administrator" \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<PRODUCTION_RESOURCE_GROUP_NAME>
```

The Bicep deployment assigns the Function App managed identity Key Vault Secrets User and Storage Blob Data Owner roles for runtime/deployment access.

## 5. Add GitHub repository variables

Add these as repository variables, not secrets:

```text
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
AZURE_LOCATION
```

No Azure client secret is required. The `azure/login` action exchanges GitHub's OIDC token for an Azure access token.

## 6. Add GitHub environment variables

Create GitHub environments named:

```text
dev
staging
production
```

Add these variables to each environment:

```text
AZURE_RESOURCE_GROUP
ENVIRONMENT_NAME
BICEP_PARAMETERS_FILE
```

Recommended values:

| GitHub environment | `ENVIRONMENT_NAME` | `BICEP_PARAMETERS_FILE` |
| --- | --- | --- |
| `dev` | `dev` | `infra/dev.bicepparam` |
| `staging` | `staging` | `infra/staging.bicepparam` |
| `production` | `prod` | `infra/prod.bicepparam` |

Each environment should point `AZURE_RESOURCE_GROUP` to its own resource group. Each resource group deployment creates its own Key Vault.
The workflows validate that `ENVIRONMENT_NAME` and `BICEP_PARAMETERS_FILE` match the GitHub environment before Azure login.

## 7. Configure production protection

In GitHub, open **Settings -> Environments -> production** and require reviewer approval before deployments. Production is manual-only and should not run until the environment approval passes.

## 8. Verify workflow permissions

The deploy workflow requires:

```yaml
permissions:
  id-token: write
  contents: read
```

The dev workflow runs automatically only on `main`. Staging and production are `workflow_dispatch` only, and their jobs skip unless the selected ref is `main`.

The auto-PR workflow intentionally uses only:

```yaml
permissions:
  contents: read
  pull-requests: write
```
