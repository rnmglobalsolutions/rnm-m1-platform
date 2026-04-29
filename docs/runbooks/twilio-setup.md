# Twilio Setup

## M1 Requirement

- One Twilio subaccount per tenant.
- One phone number minimum per tenant.
- USA tenants must use 10DLC numbers.
- Store all credentials in Azure Key Vault.
- Never commit Twilio secrets.

## Scaffold Notes

The sample tenant config stores only Key Vault secret names, not secret values.
