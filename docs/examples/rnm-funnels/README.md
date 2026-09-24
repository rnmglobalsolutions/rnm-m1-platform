# RNM Public Funnel Pages

Reference frontend examples for the two public funnel pages:

- `/consultation`
- `/masterclass/register`

Copy/adapt these examples into the separate RNM website project. M1 does not
host the RNM website. The final pages call M1 public funnel endpoints and must
not contain platform secrets, webhook secrets, class registration secrets, or
internal API keys.

## Configure

Edit `config.js` before deployment:

```js
window.RNM_FUNNEL_CONFIG = {
  API_BASE_URL: "https://<function-app>.azurewebsites.net/api",
  TENANT_ID: "rnm-insurance-agents",
  MASTERCLASS_SESSION_ID: "replace-with-published-session-id"
};
```

`MASTERCLASS_SESSION_ID` must be created first through M1 class session setup.

## Local Preview

Open these files directly in a browser for visual review:

```text
consultation/index.html
masterclass/register/index.html
```

Form submission requires a deployed M1 Function App because browser CORS only
allows the RNM production origins.

## Security

- Do not add `X-RNM-ManyChat-Secret`.
- Do not add `X-RNM-Class-Registration-Secret`.
- Do not add `x-rnm-api-key`.
- Keep `companyWebsiteConfirm` as an empty hidden honeypot field.
- Do not collect SSN, medical details, exact income, exact debt, policy
  numbers, or detailed assets in these forms.
