# Brief: master classes

Sessions, registration, reminders, attendance, and class reports.

## Key files

| Area | Files |
|---|---|
| Models and store port | `src/RNM.Platform.Application/Classes/ClassModels.cs`, `src/RNM.Platform.Application/Ports/Classes/IClassSessionStore.cs` |
| Registration | `src/RNM.Platform.Application/Classes/ClassRegistrationService.cs`, `src/RNM.Platform.Api/Functions/ClassRegistrationFunction.cs`, `PublicFunnelFunction.cs` (web form) |
| Notifications and reminders | `src/RNM.Platform.Application/Classes/ClassNotificationService.cs`, `ClassReminderService.cs`, `src/RNM.Platform.Api/Functions/ClassReminderFunction.cs` (5-minute timer) |
| Sessions | `src/RNM.Platform.Api/Functions/ClassSessionFunction.cs` (internal API key) |
| Storage | `src/RNM.Platform.Infrastructure/Classes/AzureTableClassSessionStore.cs` |
| Report | `src/RNM.Platform.Application/Classes/ClassReportService.cs` |
| Runbook | `docs/runbooks/masterclass-automation.md` |

## Invariants

- Capacity is enforced on every registration path.
- Class SMS requires explicit SMS consent (`ClassRegistrationConfirmation`,
  `ClassReminder` categories in `SmsEligibilityGate`).
- Meetings run in the tenant's own Zoom account or the tenant's Google Meet (ADR 0003).

## Known traps

- The meeting link is pasted by hand (`ZoomUrl`); nothing creates the meeting (item 9).
- Sessions never touch the calendar, so a 1:1 can be booked during a class (item 9).
- No attendance: M1 does not know who attended (item 10). People who join without
  signing in have no email; those stay `unknown`.
- Registration works from the web form and the class webhook only; ManyChat returns a
  link and voice has no tool (item 8).
- The timer processes only tenants in `RNM_ACTIVE_TENANTS` and catches only
  `ConfigurationException`, so another exception for one tenant stops the run for the
  tenants after it.
