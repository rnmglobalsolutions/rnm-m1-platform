# Zoom Tenant Setup

How a tenant connects its own Zoom account to M1. Decision and reasons: ADR 0003.

Do this on a short video call with the tenant's Zoom admin (about 15 minutes). The
tenant clicks; you guide.

## Before the call

Confirm with the tenant:

- Zoom plan is **Pro or higher**. The free plan limits meeting length and does not
  allow reading attendance.
- Who the Zoom account **owner or admin** is. That person must create the app.
- How many advisors take appointments. Each advisor needs their own licensed Zoom user.
- Expected master class size.

## Create the Server-to-Server OAuth app

1. Go to **marketplace.zoom.us** and sign in with the tenant's admin account.
2. Lower-left corner: click **Developers** to open **Created Apps**. If it does not
   appear, the user lacks the permission to view and edit Server-to-Server OAuth apps;
   use the account owner, or grant it in User Management > Roles.
3. Upper-left, next to **Developer**: click the blue **+**, then **Build an app**.
4. Choose **Server-to-Server OAuth app** and click **Create**.
5. **App credentials:** note the **Account ID**, **Client ID**, and **Client Secret**.
6. **Information:** short description, company name, developer name and email (name
   and email are required to activate).
7. **Feature:** leave event subscriptions off.
8. **Scopes:** click **Add Scopes**, search "meeting", and add the scopes to:
   - create meetings
   - update meetings
   - delete meetings
   - read past meeting participants (attendance)

   Exact scope names: confirm in Zoom's docs and list them here when item 4 of the
   backlog is built.
9. **Activation:** fix anything marked in red and click **Activate**. The app does not
   work until it is activated.

## After the call

1. The tenant sends the three values through a secure channel, never plain email.
2. Store them in Key Vault as `rnm-tenant-{tenant}-zoom-credentials`.
3. Set the tenant's appointment type to `zoom` and its Zoom host user(s) in the tenant
   config.
4. Run the new tenant setup script (backlog item 12). It tests the credentials and
   reports any failure.

## Common problems

| Problem | Fix |
|---|---|
| "Developers" or "Build an app" is missing | Use the account owner, or grant the Server-to-Server OAuth permission to the user's role |
| Credentials rejected | The app was not activated, or the secret was regenerated |
| Connection breaks later | The app was deactivated, or its creator lost the admin role (Zoom removes admin scopes). Recreate the app with the account owner |
| Attendance is empty | Free plan, missing participants scope, or attendees joined without signing in |
