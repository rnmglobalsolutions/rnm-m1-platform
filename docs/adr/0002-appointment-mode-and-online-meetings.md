# ADR 0002: Appointment Mode And Online Meetings

## Status

Accepted

## Context

M1 currently focuses on HVAC inbound booking. HVAC appointments are onsite service visits, so the customer expects a technician at the service address, not an online meeting.

Future tenants and verticals may require virtual appointments, such as sales demos, consultations, legal intake, insurance reviews, or SaaS onboarding. Those flows may need Zoom, Google Meet, or Microsoft Teams links.

## Decision

M1 must treat appointment modality as tenant or vertical configuration, not provider-specific behavior hardcoded into the booking adapter.

The current HVAC demo remains:

```json
{
  "appointmentMode": "onsite",
  "createOnlineMeeting": false
}
```

Future online appointment tenants may use:

```json
{
  "appointmentMode": "online",
  "createOnlineMeeting": true,
  "onlineMeetingProvider": "Zoom",
  "onlineMeetingCredentials": "tenant-client-zoom-credentials"
}
```

Online meeting creation must be handled by a separate adapter boundary, such as:

```csharp
IOnlineMeetingAdapter
```

Potential implementations:

- `ZoomMeetingAdapter`
- `GoogleMeetMeetingAdapter`
- `TeamsMeetingAdapter`

Booking adapters are responsible for booking provider behavior. Online meeting adapters are responsible for meeting-link behavior.

## Flow

For onsite appointments:

```text
Availability -> confirmed slot -> booking -> CRM -> SMS/email -> logs
```

For online appointments:

```text
Availability -> confirmed slot -> booking -> online meeting -> calendar update -> CRM -> SMS/email -> logs
```

The booking must remain the primary source of appointment intent. If online meeting creation fails after a booking succeeds, the system must not lose the booking or lead.

## Failure Rules

- If booking fails, do not create an online meeting.
- If online meeting creation fails after booking succeeds, keep the booking and create a retryable or human-follow-up path.
- Never silently lose the lead or booking intent.
- Log failures with `tenantId`, `verticalId`, and `correlationId`.
- Do not tell the caller they will receive a meeting link unless M1 successfully created or returned one.

## Prompt Rules

Onsite prompts must not promise online meeting links.

Online prompts may promise a meeting link only after M1 returns one.

## Consequences

- HVAC stays clean as an onsite service flow.
- Future virtual appointment flows can be added without changing the HVAC prompt or Google Calendar booking adapter responsibilities.
- Zoom, Google Meet, and Teams can be added as tenant-specific capabilities when there is direct MVP value.
- This decision avoids adding online meeting infrastructure before the current revenue path needs it.
