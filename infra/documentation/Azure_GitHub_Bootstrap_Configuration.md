# Azure GitHub Bootstrap Configuration

## Exact Order You Should Follow

### STEP 1 — Manual Azure bootstrap (do this FIRST)

You only do this once.

#### 1. Create Resource Groups

Create one resource group per deployment environment.

```bash
az group create \
  --name {PUT_HERE_YOUR_DEV_RESOURCE_GROUP_NAME} \
  --location eastus

az group create \
  --name {PUT_HERE_YOUR_STAGING_RESOURCE_GROUP_NAME} \
  --location eastus

az group create \
  --name {PUT_HERE_YOUR_PRODUCTION_RESOURCE_GROUP_NAME} \
  --location eastus
```

#### 2. Create Entra App (GitHub identity)

```bash
az ad app create --display-name "{PUT_HERE_YOUR_UNIQUE_NAME}"
```

Example:

```bash
az ad app create --display-name "rnm-github-actions"
```

Then:

```bash
az ad sp create --id <APP_ID>
```

#### 3. Assign permissions

```bash
az role assignment create \
  --assignee <APP_ID> \
  --role Contributor \
  --scope /subscriptions/<SUB_ID>/resourceGroups/{PUT_HERE_YOUR_DEV_RESOURCE_GROUP_NAME}

az role assignment create \
  --assignee <APP_ID> \
  --role Contributor \
  --scope /subscriptions/<SUB_ID>/resourceGroups/{PUT_HERE_YOUR_STAGING_RESOURCE_GROUP_NAME}

az role assignment create \
  --assignee <APP_ID> \
  --role Contributor \
  --scope /subscriptions/<SUB_ID>/resourceGroups/{PUT_HERE_YOUR_PRODUCTION_RESOURCE_GROUP_NAME}
```

#### 4. Create federated credential (VERY IMPORTANT)

This connects GitHub -> Azure:

```bash
az ad app federated-credential create \
  --id <APP_ID> \
  --parameters '{
    "name": "github-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:<your-github-username>/<repo>:ref:refs/heads/main",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

This is the step that enables secure deployments without secrets.

#### 5. Add GitHub repository variables (NOT secrets)

In your repo:

```text
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
AZURE_LOCATION
```

#### 6. Add GitHub environment variables

Create GitHub environments:

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

| GitHub environment | ENVIRONMENT_NAME | BICEP_PARAMETERS_FILE |
| --- | --- | --- |
| dev | dev | infra/dev.bicepparam |
| staging | staging | infra/staging.bicepparam |
| production | prod | infra/prod.bicepparam |

Production should require GitHub environment approval before deployment.
