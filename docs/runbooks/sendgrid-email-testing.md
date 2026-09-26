# SendGrid Email Testing

This runbook verifies RNM Platform M1 email sending through SendGrid in Azure dev.

## 1. Confirm infrastructure settings

Bicep configures `SENDGRID_API_KEY` as a Key Vault reference on both Function Apps:

```text
SENDGRID_API_KEY=@Microsoft.KeyVault(SecretUri=<KEY_VAULT_URI>secrets/rnm-sendgrid-api-key/)
```

Bicep also configures the internal API key app setting as a Key Vault reference on the main Function App only:

```text
RNM_INTERNAL_API_KEY_SECRET_NAME=@Microsoft.KeyVault(SecretUri=<KEY_VAULT_URI>secrets/rnm-internal-api-key/)
```

The contact Function App does not receive the internal API key setting because it does not host internal endpoints. The Function Apps use system-assigned managed identities to resolve Key Vault references. Bicep grants those identities the Key Vault Secrets User role on the environment Key Vault.

## 2. Add the SendGrid API key

Do not commit the SendGrid API key to GitHub, Bicep, appsettings, or tenant config.

Every environment uses the same secret name, `rnm-sendgrid-api-key`, in its own
Key Vault. Use a separate restricted ("Mail Send" only) SendGrid API key per
environment so a dev key can never send production mail.

```bash
./scripts/seed-tenant-secrets.sh --vault <KEY_VAULT_NAME> --platform
```

or directly:

```bash
az keyvault secret set \
  --vault-name <KEY_VAULT_NAME> \
  --name "rnm-sendgrid-api-key" \
  --file <(printf '%s' "$SENDGRID_API_KEY")
```

### Migrating from the per-environment secret names

Environments deployed before the rename reference `rnm-dev-sendgrid-api-key`,
`rnm-staging-sendgrid-api-key` or `rnm-prod-sendgrid-api-key`. Create
`rnm-sendgrid-api-key` in each vault **before** deploying the renamed
parameter, otherwise `SENDGRID_API_KEY` resolves to nothing and email stops:

```bash
az keyvault secret set \
  --vault-name <KEY_VAULT_NAME> \
  --name rnm-sendgrid-api-key \
  --file <(az keyvault secret show --vault-name <KEY_VAULT_NAME> --name rnm-<env>-sendgrid-api-key --query value -o tsv | tr -d '\n')
```

After the deployment, confirm readiness reports `sendGridApiKey` as passing,
then delete the old secret. Purge protection keeps it recoverable for the
retention period.

## 3. Restart the Function App

Restart after adding or rotating the secret so the Key Vault reference is refreshed quickly:

```bash
az functionapp restart \
  --resource-group <RESOURCE_GROUP_NAME> \
  --name <FUNCTION_APP_NAME>
```

## 4. Call the protected test endpoint

The test endpoint is protected by `x-rnm-api-key`. It is available outside production by default. In production, it only works when `RNM_ENABLE_TEST_EMAIL_ENDPOINT=true` is explicitly configured.

```bash
curl -X POST "https://<FUNCTION_HOST>/api/test/email/send" \
  -H "Content-Type: application/json" \
  -H "x-rnm-api-key: <INTERNAL_API_KEY>" \
  -d '{
    "toEmail": "your-test-email@example.com",
    "subject": "RNM Platform Test Email",
    "body": "This is a test email from SendGrid integration."
  }'
```

Expected success shape:

```json
{
  "sent": true,
  "providerMessageId": "<sendgrid-message-id>",
  "failureReason": null,
  "correlationId": "<correlation-id>"
}
```

Application code reads the resolved API key with:

```csharp
Environment.GetEnvironmentVariable("SENDGRID_API_KEY")
```
