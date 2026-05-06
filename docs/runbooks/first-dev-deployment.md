# First Dev Deployment

This runbook deploys the RNM Platform M1 Azure Functions backend to the Azure dev environment.

## Prerequisites

- Complete `docs/runbooks/github-oidc-bootstrap.md`.
- Dev resource group exists.
- GitHub repository variables are configured:
  - `AZURE_CLIENT_ID`
  - `AZURE_TENANT_ID`
  - `AZURE_SUBSCRIPTION_ID`
  - `AZURE_LOCATION`
- GitHub `dev` environment variables are configured:
  - `AZURE_RESOURCE_GROUP`
  - `ENVIRONMENT_NAME` = `dev`
  - `BICEP_PARAMETERS_FILE` = `infra/dev.bicepparam`
- Azure CLI is installed for local validation.
- .NET SDK version from `global.json` is installed.

## Deployment flow

- Push to `feature/**`: creates a PR to `main` if one does not already exist.
- PR to `main`: runs CI only.
- Merge to `main`: automatically deploys dev.
- Staging: manual promotion with `Deploy Staging`.
- Production: manual promotion with `Deploy Production` and GitHub environment approval.
- Dev, staging, and production each use a separate Azure resource group and separate Key Vault.

## 1. Validate Bicep locally

```bash
export AZURE_LOCATION=eastus

az bicep build --file infra/main.bicep
az bicep build-params --file infra/dev.bicepparam
```

Fix any Bicep validation errors before deploying.

## 2. Run a what-if deployment

`.bicepparam` files are deployed directly. Do not combine them with `--template-file` or inline parameter overrides.

```bash
az deployment group what-if \
  --resource-group <DEV_RESOURCE_GROUP_NAME> \
  --parameters infra/dev.bicepparam
```

Review the planned resources:

- Storage account
- Blob container for Flex deployment packages
- Log Analytics workspace
- Application Insights
- Key Vault
- Flex Consumption hosting plan
- Function App
- Contact Function App
- RBAC role assignments

## 3. Deploy infrastructure

```bash
az deployment group create \
  --resource-group <DEV_RESOURCE_GROUP_NAME> \
  --parameters infra/dev.bicepparam
```

Capture the outputs:

- `functionAppName`
- `functionAppDefaultHostName`
- `contactFunctionAppName`
- `contactFunctionAppDefaultHostName`
- `keyVaultName`
- `keyVaultUri`
- `appInsightsName`
- `storageAccountName`

## 4. Add required Key Vault secrets

Seed these secrets before a real end-to-end dev test:

```bash
az keyvault secret set --vault-name <KEY_VAULT_NAME> --name rnm-internal-api-key --value '<INTERNAL_API_KEY>'
az keyvault secret set --vault-name <KEY_VAULT_NAME> --name rnm-dev-sendgrid-api-key --value '<SENDGRID_API_KEY>'
az keyvault secret set --vault-name <KEY_VAULT_NAME> --name tenant-sample-hvac-vapi-webhook-secret --value '<VAPI_WEBHOOK_SECRET>'
az keyvault secret set --vault-name <KEY_VAULT_NAME> --name tenant-sample-hvac-twilio-account-sid --value '<TWILIO_ACCOUNT_SID>'
az keyvault secret set --vault-name <KEY_VAULT_NAME> --name tenant-sample-hvac-twilio-auth-token --value '<TWILIO_AUTH_TOKEN>'
az keyvault secret set --vault-name <KEY_VAULT_NAME> --name tenant-sample-hvac-email-connection --value '<EMAIL_CONNECTION_STRING>'
```

Bicep does not create the SendGrid secret value. It configures `SENDGRID_API_KEY` as a Key Vault reference on both Function Apps:

```text
SENDGRID_API_KEY=@Microsoft.KeyVault(SecretUri=<KEY_VAULT_URI>secrets/rnm-dev-sendgrid-api-key/)
```

Bicep also configures the internal API key setting as a Key Vault reference on the main Function App only:

```text
RNM_INTERNAL_API_KEY_SECRET_NAME=@Microsoft.KeyVault(SecretUri=<KEY_VAULT_URI>secrets/rnm-internal-api-key/)
```

The app setting name is retained for compatibility, but Azure resolves the setting to the internal API key value at runtime. The contact Function App does not receive this app setting because it does not host internal endpoints. The Function Apps use system-assigned managed identities to resolve Key Vault references. The deployment grants both identities the Key Vault Secrets User role on the environment Key Vault. Application code can read the resolved values with:

```csharp
Environment.GetEnvironmentVariable("SENDGRID_API_KEY")
Environment.GetEnvironmentVariable("RNM_INTERNAL_API_KEY_SECRET_NAME")
```

For GoHighLevel, use JSON so the adapters have the access token plus provider ids:

```bash
az keyvault secret set \
  --vault-name <KEY_VAULT_NAME> \
  --name tenant-sample-hvac-ghl-api-key \
  --value '{
    "accessToken": "<GHL_ACCESS_TOKEN>",
    "locationId": "<GHL_LOCATION_ID>",
    "calendarId": "<GHL_CALENDAR_ID>",
    "apiVersion": "2021-07-28"
  }'
```

## 5. Publish locally

```bash
dotnet restore RNM.Platform.sln
dotnet build RNM.Platform.sln --configuration Release --no-restore
dotnet test RNM.Platform.sln --configuration Release --no-build
dotnet publish src/RNM.Platform.Api/RNM.Platform.Api.csproj \
  --configuration Release \
  --no-build \
  --output ./publish-check
```

Verify config packaging:

```bash
test -d ./publish-check/config
test -f ./publish-check/config/tenants/sample-hvac-tenant.json
test -f ./publish-check/config/verticals/hvac.json
```

## 6. Deploy app package manually

Flex Consumption uses a deployment package stored in Blob storage. The GitHub workflow uses `Azure/functions-action`, which handles the supported deployment method for Flex.

For manual testing, zip the publish output:

```bash
cd publish-check
zip -r ../functionapp.zip .
```

Then deploy with the Azure Functions deployment action from GitHub Actions, or use an Azure CLI/Core Tools path that supports Flex Consumption one-deploy for your installed tool version.

## 7. Test health endpoint

The health endpoint is anonymous:

```bash
curl -i \
  https://<FUNCTION_APP_DEFAULT_HOST_NAME>/api/health
```

Expected result:

```json
{
  "status": "healthy"
}
```

If protected test endpoints return `401`, verify:

- `rnm-internal-api-key` exists in Key Vault.
- `RNM_INTERNAL_API_KEY_SECRET_NAME` is a Key Vault reference to the `rnm-internal-api-key` secret.
- The Function App managed identities have Key Vault Secrets User on the vault.
- You sent the `x-rnm-api-key` header.

## 8. Test contact Function App CORS

The public contact endpoint is deployed to a separate contact Function App. The main Function App keeps app-level CORS empty. The contact Function App allows browser calls only from:

```text
https://www.rnmglobalsolutions.com
https://rnmglobalsolutions.com
```

Allowed origin preflight:

```bash
curl -i -X OPTIONS \
  https://<CONTACT_FUNCTION_APP_DEFAULT_HOST_NAME>/api/contact/system-review \
  -H "Origin: https://www.rnmglobalsolutions.com" \
  -H "Access-Control-Request-Method: POST"
```

Expected response includes:

```text
Access-Control-Allow-Origin: https://www.rnmglobalsolutions.com
```

Disallowed origin preflight:

```bash
curl -i -X OPTIONS \
  https://<CONTACT_FUNCTION_APP_DEFAULT_HOST_NAME>/api/contact/system-review \
  -H "Origin: https://evil.example" \
  -H "Access-Control-Request-Method: POST"
```

Azure Functions may return `204 No Content`, but the response must not include `Access-Control-Allow-Origin`.

## 9. Promote to staging

Staging is not automatic. Run **Actions -> Deploy Staging -> Run workflow** from GitHub after dev is healthy.

The `staging` GitHub environment must have:

```text
AZURE_RESOURCE_GROUP=<STAGING_RESOURCE_GROUP_NAME>
ENVIRONMENT_NAME=staging
BICEP_PARAMETERS_FILE=infra/staging.bicepparam
```

Seed staging Key Vault with staging provider credentials before end-to-end testing.

## 10. Promote to production

Production is not automatic. Run **Actions -> Deploy Production -> Run workflow** from GitHub after staging has been accepted.

The `production` GitHub environment must have:

```text
AZURE_RESOURCE_GROUP=<PRODUCTION_RESOURCE_GROUP_NAME>
ENVIRONMENT_NAME=prod
BICEP_PARAMETERS_FILE=infra/prod.bicepparam
```

Configure required reviewers on the GitHub `production` environment. The production workflow will pause until approval is granted. Seed production Key Vault with production provider credentials before live traffic.
