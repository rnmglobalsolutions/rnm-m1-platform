# Twilio SMS Setup - yartex

Este runbook describe cómo habilitar SMS con Twilio para el tenant
`yartex`, incluyendo cumplimiento A2P 10DLC, secretos, configuración
del tenant, webhooks, automatizaciones y verificación end-to-end.

## Estado actual

El tenant ya selecciona los proveedores correctos:

```json
{
  "smsProvider": "Twilio",
  "emailProvider": "SendGrid"
}
```

Sin embargo, no está listo para producción mientras estos valores continúen como
placeholders:

```json
{
  "smsFromPhoneNumber": "+1XXXXXXXXXX",
  "businessNotificationPhoneNumber": "+1XXXXXXXXXX"
}
```

La configuración se encuentra en:

```text
config/tenants/yartex.json
```

## 1. Crear el subaccount de Twilio

En la cuenta principal de RNM, crear un subaccount dedicado:

```text
RNM Parent Account
└── Subaccount: yartex
```

Usar un subaccount por cliente o tenant. Esto separa credenciales, tráfico,
reputación, cumplimiento y problemas operativos.

Las credenciales que M1 utilizará deben pertenecer al subaccount:

- Account SID
- Auth Token

No utilizar las credenciales de la cuenta principal.

Referencia oficial:
[Twilio ISV A2P onboarding](https://www.twilio.com/docs/messaging/compliance/a2p-10dlc/onboarding-isv)

## 2. Registrar el negocio para A2P 10DLC

Dentro del subaccount:

1. Crear el Customer Profile.
2. Registrar el Brand que aparecerá como remitente de los mensajes.
3. Crear una Campaign que describa el uso real: registros, confirmaciones,
   reminders y follow-ups.
4. Proporcionar ejemplos reales de mensajes.
5. Documentar el mecanismo mediante el cual el lead entrega consentimiento.
6. Incluir las instrucciones de `STOP` y `HELP` requeridas.
7. Esperar la aprobación antes de enviar tráfico real a clientes.

Si este tenant representa legalmente a Yartex, registrar a Yartex como Brand. Agencias
independientes que envían bajo su propia marca no deben compartir este Brand o
Campaign; deben tener su propio tenant, subaccount y registro A2P.

Referencia oficial:
[Twilio A2P 10DLC](https://www.twilio.com/docs/messaging/compliance/a2p-10dlc)

## 3. Crear el Messaging Service y comprar el número

Dentro del subaccount:

1. Ir a `Messaging > Services`.
2. Crear un servicio llamado `yartex-messaging`.
3. Comprar un número local de Estados Unidos con capacidad SMS.
4. Agregar el número al Sender Pool del Messaging Service.
5. Asociar el Messaging Service con la Campaign aprobada.
6. Guardar el número en formato E.164, por ejemplo `+19565551234`.

M1 actualmente utiliza el número configurado como `From` y envía `From`, `To` y
`Body` directamente a la API de Twilio.

## 4. Configurar el webhook de mensajes entrantes

En la configuración del número Twilio, establecer:

```text
A message comes in: Webhook
Method: POST
URL: https://<FUNCTION_APP_HOST>/api/tenants/yartex/webhooks/twilio/sms-inbound
```

Este endpoint valida `X-Twilio-Signature` y procesa estos comandos de opt-out:

- `STOP`
- `STOPALL`
- `UNSUBSCRIBE`
- `CANCEL`
- `END`
- `QUIT`

Cuando recibe uno de estos comandos, M1 marca el contacto como `opted_out` y
bloquea futuros SMS al cliente.

Twilio calcula la firma utilizando la URL exacta configurada. No cambiar el host,
path, protocolo o query string sin actualizar también la configuración en Twilio.

Referencia oficial:
[Twilio webhook security](https://www.twilio.com/docs/usage/webhooks/webhooks-security)

## 5. Guardar las credenciales en Azure Key Vault

Crear exactamente estos secretos en el Key Vault del ambiente correspondiente:

```text
tenant-yartex-twilio-account-sid
tenant-yartex-twilio-auth-token
```

Valores esperados:

```text
tenant-yartex-twilio-account-sid = ACxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
tenant-yartex-twilio-auth-token = <SUBACCOUNT_AUTH_TOKEN>
```

Usar Azure Portal cuando sea posible para evitar colocar el Auth Token en el
historial del terminal. Nunca guardar estos valores en GitHub, archivos JSON o
Postman.

Confirmar que la Managed Identity de la Function App puede leer ambos secretos.

## 6. Actualizar la configuración del tenant

Editar `config/tenants/yartex.json`:

```json
{
  "communication": {
    "smsFromPhoneNumber": "+19565551234",
    "businessNotificationPhoneNumber": "+1NUMERO_REAL_DEL_DUENO"
  }
}
```

Significado:

- `smsFromPhoneNumber`: número comprado en Twilio.
- `businessNotificationPhoneNumber`: celular del dueño u operador que recibirá
  alertas de nuevos leads; no tiene que ser un número Twilio.

No usar el número Twilio como destinatario de sus propias notificaciones.

Hacer commit y deploy después del cambio porque la configuración del tenant se
empaqueta con la aplicación.

## 7. Activar reminders y follow-ups

Las confirmaciones inmediatas no dependen de `RNM_ACTIVE_TENANTS`. Los reminders
y follow-ups automáticos sí dependen de esa configuración.

Dev ya incluye este tenant. Para staging o producción, agregarlo al parámetro
`activeTenants` del ambiente correspondiente:

```bicep
param activeTenants = 'yartex'
```

Esto configura la Function App con:

```text
RNM_ACTIVE_TENANTS=yartex
```

Si ya existen otros tenants activos, mantenerlos como una lista separada por
comas.

## 8. Ejecutar el preflight

Desde la raíz del repositorio:

```bash
dotnet run \
  --project tools/RNM.Platform.TenantPreflight/RNM.Platform.TenantPreflight.csproj \
  -- \
  --tenant yartex \
  --environment production
```

Resultado esperado:

```text
valid
```

No desplegar o activar tráfico si el resultado es `warning` o `blocked`. Los
números `+1XXXXXXXXXX` bloquean correctamente el preflight de producción.

## 9. Desplegar y comprobar readiness

Después del deploy, llamar el endpoint protegido:

```bash
curl \
  -H "x-rnm-api-key: <INTERNAL_API_KEY>" \
  "https://<FUNCTION_APP_HOST>/api/tenants/yartex/readiness"
```

No habilitar tráfico real hasta recibir:

```json
{
  "status": "ready"
}
```

El preflight valida el manifiesto local. Readiness valida la configuración y los
secretos disponibles en el ambiente desplegado.

## 10. Probar primero Twilio de forma aislada

Antes de probar M1:

1. Enviar un SMS desde Twilio Console al teléfono de prueba.
2. Confirmar que Twilio acepta el mensaje.
3. Confirmar que el teléfono recibe el mensaje.
4. Revisar el Message SID y el estado en Twilio Messaging Logs.

Esto permite separar problemas de Twilio/A2P de problemas de configuración de
M1.

## 11. Probar el flujo completo de M1

M1 no expone un endpoint para enviar SMS arbitrarios. Los mensajes se envían por
flujos controlados: registros, bookings, confirmaciones, reminders y follow-ups.

Para probar el registro de una masterclass desde el flujo publico del website:

```text
POST /api/tenants/yartex/funnels/masterclass/{classSessionId}/registrations
```

Headers:

```text
Content-Type: application/json
Origin: https://rnmglobalsolutions.com
```

No incluyas `X-RNM-Class-Registration-Secret` ni `x-rnm-api-key` en JavaScript
del website. For an internal Postman/server-to-server test, the direct class
registration endpoint still accepts `x-rnm-api-key` or
`X-RNM-Class-Registration-Secret`.

Body de prueba:

```json
{
  "customerName": "Test Lead",
  "customerPhoneNumber": "+1XXXXXXXXXX",
  "customerEmail": "test@example.com",
  "campaignId": "financial-video-v1",
  "funnelType": "financial_education",
  "primaryGoal": "family_protection",
  "timeline": "under_30_days",
  "state": "TX",
  "consentSms": true,
  "consentEmail": true,
  "consentTextVersion": "web-funnel-v1",
  "consentDisclosureText": "Acepto que Yartex me contacte por SMS/email sobre mi solicitud. Pueden aplicar tarifas de mensajes y datos. Puedo responder STOP para optar por salir.",
  "companyWebsiteConfirm": ""
}
```

Verificar:

1. El endpoint responde `200 OK` y `succeeded: true`.
2. El resultado del canal SMS aparece como enviado.
3. Twilio devuelve un Message SID.
4. El teléfono recibe el mensaje.
5. Application Insights contiene `sms.confirmation.sent`.
6. El timeline del CRM contiene `sms.sent`.
7. El mensaje identifica al remitente e incluye `Reply STOP to opt out`.

Los SMS de marketing, reminders y follow-ups requieren `smsConsentStatus=opt_in`.
Usar únicamente números cuyos propietarios hayan otorgado consentimiento.
Si `consentSms` es `true` pero falta `consentDisclosureText` o
`consentTextVersion`, el lead se guarda igual, el SMS queda como no autorizado
y Application Insights registra `lead_intake.consent_evidence_missing`.

Cada SMS pasa por la compuerta de elegibilidad. Cuando se bloquea, el timeline
del CRM registra `sms.skipped` con el motivo (`ContactOptedOut`,
`ConsentNotGranted`, `OutsideSendWindow`, `RetryStale`, etc.) y Application
Insights registra `sms.eligibility.skipped`. La confirmación de una cita es
transaccional: solo la bloquea un opt-out explícito del número, no una falla del
CRM.

## 12. Probar STOP y la supresión futura

Desde el teléfono de prueba, responder:

```text
STOP
```

Verificar:

1. Twilio llama al endpoint `sms-inbound`.
2. M1 responde `202 Accepted`.
3. El contacto queda con `consentStatus=opted_out`.
4. El timeline contiene el evento de consentimiento correspondiente.
5. Un follow-up posterior omite el SMS.

Usar un contacto de prueba dedicado. El opt-out se conserva deliberadamente y no
debe revertirse mediante una edición informal del registro.

## 13. Delivery status: gap operativo actual

M1 ya tiene este endpoint:

```text
POST /api/tenants/yartex/webhooks/twilio/sms-status
```

Sin embargo, `TwilioSmsSender` actualmente no envía `StatusCallback` ni utiliza
`MessagingServiceSid`; envía el número directamente mediante `From`. Por eso no
se debe asumir que los eventos `delivered`, `undelivered` o `failed` llegarán
automáticamente por configurar el callback del Messaging Service.

El envío de SMS funciona sin este callback. Para garantizar telemetría de entrega
queda pendiente uno de estos cambios:

1. Agregar `StatusCallback` a cada solicitud de envío; o
2. Configurar M1 para enviar mediante `MessagingServiceSid` y usar el callback del
   Messaging Service.

Referencia oficial:
[Twilio Messaging Services](https://www.twilio.com/docs/messaging/services)

## Checklist de activación

- [ ] Subaccount dedicado creado.
- [ ] Customer Profile aprobado.
- [ ] Brand aprobado.
- [ ] Campaign aprobada.
- [ ] Messaging Service creado.
- [ ] Número SMS comprado y asociado.
- [ ] Webhook `sms-inbound` configurado con `POST`.
- [ ] Account SID guardado en Key Vault.
- [ ] Auth Token guardado en Key Vault.
- [ ] `smsFromPhoneNumber` reemplazado.
- [ ] `businessNotificationPhoneNumber` reemplazado.
- [ ] Tenant incluido en `RNM_ACTIVE_TENANTS` donde corresponda.
- [ ] Preflight devuelve `valid`.
- [ ] Readiness devuelve `ready`.
- [ ] Prueba aislada de Twilio completada.
- [ ] Prueba end-to-end desde M1 completada.
- [ ] STOP persiste `opted_out` y bloquea el siguiente SMS.
- [ ] Delivery status gap aceptado o corregido antes del go-live.
