# M1 Tenant Pricing Strategy

> Estado: propuesta base para análisis y comparación. No constituye una tarifa
> contractual definitiva.
>
> Versión: 1.0  
> Fecha de referencia: 2026-09-25  
> Moneda: USD

## Objetivo

Definir una referencia inicial para:

- clasificar tenants por consumo;
- estimar el costo directo mensual de M1;
- establecer precios de implementación, suscripción y sobreuso;
- preservar margen para soporte, optimización y crecimiento;
- comparar posteriormente esta propuesta con otras variantes comerciales.

Las categorías `pequeno`, `normal` e `intensivo` describen consumo mensual. No
describen la cantidad de empleados ni los ingresos del cliente.

## Supuestos De Consumo

Para convertir minutos de voz en llamadas se utiliza una duración promedio de
4 a 5 minutos por llamada.

| Categoría | Voz al mes | Llamadas aproximadas | Contactos activos en ManyChat | Segmentos SMS | Emails | Costo directo estimado |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Pequeño | 100 min | 20-25 | 250 | 200 | 200 | $45-$65 |
| Normal | 500 min | 100-125 | 1,000 | 1,000 | 1,000 | $90-$140 |
| Intensivo | 2,000 min | 400-500 | 5,000 | 4,000 | 4,000 | $300-$455 |

Un negocio con muchos empleados puede ser un tenant pequeño si genera poco
tráfico. Una agencia de dos personas puede ser intensiva si mantiene campañas
activas y recibe cientos de llamadas o conversaciones al mes.

### Definiciones De Medición

- **Minuto de voz:** minuto procesado por la cadena Vapi, telefonía, STT, LLM y
  TTS.
- **Contacto activo:** persona que interactúa con una automatización de
  ManyChat durante el ciclo mensual, de acuerdo con la definición de ManyChat.
- **Segmento SMS:** unidad facturable de Twilio. Un mensaje visible puede
  producir varios segmentos por longitud o codificación.
- **Email:** confirmación, recordatorio o notificación transaccional enviada por
  la plataforma.

## Costo Directo Por Servicio

| Servicio | Tenant pequeño | Tenant normal | Tenant intensivo |
| --- | ---: | ---: | ---: |
| Voz: Vapi, Claude, Deepgram, ElevenLabs y transporte | $9-$14 | $45-$70 | $180-$280 |
| ManyChat | $29 | $29 | $69 |
| Twilio: número, A2P 10DLC y SMS | $5-$14 | $13-$26 | $43-$71 |
| Azure compartido | $1-$3 | $2-$8 | $8-$25 |
| SendGrid compartido | $0.10-$2 | $0.50-$3 | $2-$8 |
| Google Calendar, cuenta del cliente | $0 | $0 | $0 |
| Zoom, cuenta del cliente | $0 | $0 | $0 |
| **Total mensual estimado** | **$45-$65** | **$90-$140** | **$300-$455** |

### Voz

Se recomienda presupuestar entre **$0.09 y $0.14 por minuto** para la cadena
completa de voz:

- Vapi;
- Deepgram para speech-to-text;
- Claude Sonnet para razonamiento;
- ElevenLabs para text-to-speech;
- transporte telefónico de Twilio.

Estos componentes forman parte del costo completo de la llamada. No deben
sumarse nuevamente después de calcular el costo por minuto. Una suscripción de
Vapi y capacidad adicional de concurrencia se tratarían como costos compartidos
de plataforma, salvo que se cree una cuenta separada para cada tenant.

### ManyChat

La referencia operativa es ManyChat Pro, aproximadamente $29 mensuales, para
tenants de uso pequeño o normal. Alrededor de 5,000 contactos activos, un plan
superior cercano a $69 mensuales puede resultar más económico que pagar
overages sobre Pro.

Se presupone una cuenta o workspace asociado a las cuentas sociales de cada
tenant. El precio real debe confirmarse durante el onboarding porque puede
variar según la fecha de creación de la cuenta y el plan disponible.

### Twilio

El costo por tenant incluye:

- al menos un número telefónico local;
- una campaña A2P 10DLC cuando corresponda;
- segmentos SMS enviados y recibidos;
- cargos aplicables del carrier.

Para planificación se recomienda usar **$0.01-$0.015 por segmento SMS**, aunque
la tarifa base publicada pueda ser menor. El registro inicial de A2P 10DLC es
un costo de onboarding, no un costo mensual recurrente.

### Azure

Azure opera como infraestructura compartida. No se despliega una plataforma
completa independiente por tenant. El costo incluye Azure Functions Flex
Consumption, Storage Tables y Queues, Key Vault, Application Insights y Log
Analytics.

La asignación estimada es:

| Cantidad de tenants | Azure asignado por tenant al mes |
| --- | ---: |
| 10 | $2-$10 |
| 50 | $0.50-$3 |
| 300 | $0.25-$2, antes de capacidad adicional |

Application Insights y Log Analytics probablemente serán el componente de
Azure que más crezca si se registran demasiados payloads o trazas. Al llegar a
300-500 tenants también puede ser necesario particionar el procesamiento de
recordatorios o introducir más trabajo basado en colas.

### SendGrid

La arquitectura actual permite compartir una cuenta de SendGrid entre tenants.
El costo mensual del plan se distribuye de acuerdo con el volumen de emails de
cada tenant. Una cuenta dedicada por tenant debe cotizarse aparte porque elimina
esta economía compartida.

### Google Calendar Y Zoom

El costo incremental para RNM es $0 cuando el cliente conecta sus propias
cuentas. Si RNM proporciona las licencias, deben añadirse al contrato o cobrarse
directamente:

- Google Workspace: aproximadamente $7-$8.40 por usuario al mes;
- Zoom Pro: aproximadamente $14.16-$16.99 por host al mes.

Una sesión de masterclass utiliza un enlace compartido por todos sus
registrados. No se necesita una licencia de Zoom por participante.

## Propuesta Comercial Base

M1 no debe venderse como una reventa de APIs. El servicio entrega un flujo de
captura de ingresos:

- atención telefónica 24/7;
- captura desde Facebook e Instagram;
- calificación de prospectos;
- validación de reglas del negocio;
- reserva de citas o registro en masterclasses;
- sincronización con CRM;
- confirmaciones y recordatorios;
- trazabilidad, recuperación ante fallos y soporte operativo.

### Planes Recomendados

| Plan | Precio mensual | Minutos de voz | Contactos ManyChat activos | Segmentos SMS |
| --- | ---: | ---: | ---: | ---: |
| M1 Launch | **$697** | 500 | 1,000 | 1,000 |
| M1 Growth | **$1,297** | 1,500 | 2,500 | 3,000 |
| M1 Scale | **$2,497** | 4,000 | 7,500 | 8,000 |

Los emails transaccionales dentro de un uso razonable pueden incluirse sin
presentarlos como una unidad comercial principal. Todos los planes deben estar
limitados por una política de uso razonable y nunca anunciarse como ilimitados.

### Margen Bruto Estimado

| Plan | Costo directo aproximado | Precio mensual | Margen bruto antes de soporte y ventas |
| --- | ---: | ---: | ---: |
| M1 Launch | $90-$140 | $697 | 80%-87% |
| M1 Growth | $205-$320 | $1,297 | 75%-84% |
| M1 Scale | $530-$825 | $2,497 | 67%-79% |

Formula:

```text
margen bruto = (precio - costo directo) / precio
```

El margen anterior todavía debe financiar onboarding, soporte, ventas,
incidentes, desarrollo continuo, procesamiento de pagos, impuestos y consumo
inesperado.

## Implementación

| Tipo | Precio recomendado |
| --- | ---: |
| Configuración estándar | $1,500-$2,500 |
| CRM o flujo complejo | $3,000-$5,000 |
| Integración personalizada | Cotización |

La configuración estándar debe cubrir:

- tenant y configuración vertical;
- prompts y reglas de calificación;
- número telefónico y A2P 10DLC;
- ManyChat;
- calendario y CRM soportados;
- plantillas de SMS y email;
- pruebas end-to-end y salida a producción.

La oferta estándar recomendada es:

> **$2,000 de implementación más $697 mensuales por tenant.**

## Sobreuso Y Servicios Adicionales

| Concepto | Precio recomendado |
| --- | ---: |
| Minuto adicional de voz | $0.25 |
| Segmento SMS adicional | $0.04 |
| Número telefónico adicional | $15/mes |
| Contactos ManyChat adicionales | Cambio de plan o costo real más 20% |
| Trabajo fuera del alcance | $150-$200/hora |
| Google Workspace o Zoom suministrado por RNM | Costo de licencia más administración |

La inversión publicitaria de Meta siempre debe ser pagada directamente por el
cliente y no formar parte del precio de M1.

## Estrategia Para Los Primeros Clientes

Para validar resultados comerciales sin fijar permanentemente un precio bajo:

- ofrecer a los primeros 3-5 clientes un piloto de 90 días;
- cobrar $1,500 de implementación;
- cobrar $497 al mes durante el piloto;
- incluir 300 minutos, 500 contactos activos y 500 segmentos SMS;
- migrar a $697 al mes al finalizar el piloto;
- limitar el descuento anual a 10%, con pago anticipado.

Yartex puede funcionar como tenant interno de validación. Sus costos deben
medirse, aunque no exista una factura comercial entre Yartex y RNM.

## Posicionamiento

Herramientas de recepción AI más limitadas pueden comenzar cerca de $79-$150 al
mes, pero suelen ser autoservicio o añadir cargos por llamada. M1 combina voz,
captura social, calificación, booking, CRM, mensajería, recordatorios y operación
administrada. Por eso no debe competir exclusivamente por precio con una
herramienta aislada.

Un precio de $99-$299 al mes dejaría poco margen para personalización y soporte
y posicionaría M1 como software genérico en lugar de una solución administrada
de captura de ingresos.

## Exclusiones

Las estimaciones no incluyen:

- inversión en anuncios de Meta;
- trabajo de agentes humanos;
- impuestos;
- comisiones de cobro;
- licencias dedicadas no indicadas;
- desarrollos o integraciones personalizados;
- SLA empresarial o soporte fuera de horario;
- costos legales y regulatorios específicos de una industria.

## Métricas Que Deben Reemplazar Los Supuestos

Después de operar los primeros tenants deben medirse mensualmente:

- minutos y duración promedio por llamada;
- llamadas atendidas, calificadas y convertidas;
- costo real de voz por minuto;
- contactos activos de ManyChat;
- segmentos SMS por mensaje y por booking;
- emails enviados y reintentos;
- costo de Azure por recurso y por tenant;
- volumen de logs por tenant;
- horas de soporte y optimización;
- citas o registros generados;
- ingreso atribuible y retorno para el cliente;
- margen bruto real por tenant.

La clasificación y los límites de cada plan deben revisarse después de 60-90
días de datos productivos.

## Plantilla Para Comparar Variantes

| Criterio | Propuesta base | Variante A | Variante B |
| --- | ---: | ---: | ---: |
| Setup estándar | $2,000 |  |  |
| Precio mensual principal | $697 |  |  |
| Minutos incluidos | 500 |  |  |
| Contactos activos incluidos | 1,000 |  |  |
| Segmentos SMS incluidos | 1,000 |  |  |
| Costo directo esperado | $90-$140 |  |  |
| Margen bruto esperado | 80%-87% |  |  |
| Minuto adicional | $0.25 |  |  |
| Segmento SMS adicional | $0.04 |  |  |
| Compromiso contractual | Mensual |  |  |
| Descuento anual máximo | 10% |  |  |

## Fuentes De Referencia

- [Vapi pricing](https://vapi.ai/pricing)
- [Deepgram pricing](https://deepgram.com/pricing)
- [Claude Sonnet pricing](https://www.anthropic.com/claude/sonnet)
- [ElevenLabs API pricing](https://elevenlabs.io/pricing/api)
- [ManyChat pricing](https://manychat.com/pricing)
- [ManyChat active contacts](https://help.manychat.com/hc/en-us/articles/25800323349020-Active-Contacts)
- [Twilio SMS pricing](https://www.twilio.com/en-us/sms/pricing/us)
- [Twilio Voice pricing](https://www.twilio.com/en-us/voice/pricing/us)
- [Twilio A2P 10DLC](https://www.twilio.com/en-us/phone-numbers/a2p-10dlc)
- [Azure Functions pricing](https://azure.microsoft.com/en-us/pricing/details/functions/)
- [Azure Table Storage pricing](https://azure.microsoft.com/en-us/pricing/details/storage/tables/)
- [Azure Key Vault pricing](https://azure.microsoft.com/en-us/pricing/details/key-vault/)
- [Azure Monitor pricing](https://azure.microsoft.com/en-us/pricing/details/monitor/)
- [SendGrid pricing](https://static0.twilio.com/en-us/products/email-api/pricing)
- [Google Workspace pricing](https://workspace.google.com/pricing.html?tab_activeEl=tabset-companies)
- [Zoom pricing](https://www.zoom.com/en/products/virtual-meetings/)
- [Smith.ai AI Receptionist pricing](https://smith.ai/ai-receptionist)
- [Goodcall pricing](https://www.goodcall.com/pricing)

## Decisión Provisional

La referencia comercial inicial de M1 es **$2,000 de implementación y $697 al
mes por tenant**, con límites de consumo y sobreuso explícitos. Esta decisión
debe contrastarse con variantes basadas en valor, volumen o industria después
de obtener datos reales de los primeros pilotos.
