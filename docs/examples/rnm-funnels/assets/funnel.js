(function () {
  const config = window.RNM_FUNNEL_CONFIG || {};
  const apiBaseUrl = (config.API_BASE_URL || "").replace(/\/$/, "");
  const tenantId = config.TENANT_ID || "rnm-insurance-agents";

  function endpoint(path) {
    if (!apiBaseUrl) {
      throw new Error("API_BASE_URL is not configured.");
    }

    return `${apiBaseUrl}/tenants/${encodeURIComponent(tenantId)}${path}`;
  }

  function formData(form) {
    const data = new FormData(form);
    return Object.fromEntries(data.entries());
  }

  function setStatus(form, message, kind) {
    const status = form.querySelector("[data-status]");
    if (!status) return;
    status.textContent = message;
    status.className = `status ${kind || ""}`.trim();
  }

  function setBusy(form, busy) {
    const button = form.querySelector("button[type='submit']");
    if (!button) return;
    button.disabled = busy;
    button.textContent = busy ? "Enviando..." : button.dataset.defaultText;
  }

  function consentPayload(values) {
    const consent = values.consent === "on";
    return {
      marketingConsentGranted: consent,
      consentSms: consent,
      consentEmail: consent,
      consentTextVersion: "web-funnel-v1"
    };
  }

  async function postJson(url, payload) {
    const response = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify(payload)
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      const code = body.validationResult || body.code || "request_failed";
      throw new Error(code);
    }

    return body;
  }

  function initConsultationForm(form) {
    form.addEventListener("submit", async function (event) {
      event.preventDefault();
      const values = formData(form);
      const payload = {
        submissionId: crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`,
        customerName: values.customerName,
        customerPhoneNumber: values.customerPhoneNumber,
        customerEmail: values.customerEmail,
        campaignId: values.campaignId || "web-consultation",
        funnelType: values.funnelType || "financial_education",
        primaryGoal: values.primaryGoal,
        timeline: values.timeline,
        state: values.state,
        currentProtection: values.currentProtection,
        monthlyRange: values.monthlyRange,
        experienceLevel: values.experienceLevel,
        weeklyAvailability: values.weeklyAvailability,
        companyWebsiteConfirm: values.companyWebsiteConfirm || "",
        ...consentPayload(values)
      };

      setBusy(form, true);
      setStatus(form, "", "");
      try {
        await postJson(endpoint("/funnels/consultation"), payload);
        setStatus(form, "Recibimos tu solicitud. Nuestro equipo te contactara con el siguiente paso.", "ok");
        form.reset();
      } catch (error) {
        setStatus(form, "No pudimos enviar la solicitud. Revisa los campos e intenta otra vez.", "error");
      } finally {
        setBusy(form, false);
      }
    });
  }

  function initMasterclassForm(form) {
    form.addEventListener("submit", async function (event) {
      event.preventDefault();
      const sessionId = config.MASTERCLASS_SESSION_ID;
      if (!sessionId || sessionId === "replace-with-published-session-id") {
        setStatus(form, "La sesion todavia no esta configurada.", "error");
        return;
      }

      const values = formData(form);
      const payload = {
        customerName: values.customerName,
        customerPhoneNumber: values.customerPhoneNumber,
        customerEmail: values.customerEmail,
        campaignId: values.campaignId || "web-masterclass",
        funnelType: values.funnelType || "financial_education",
        primaryGoal: values.primaryGoal,
        timeline: values.timeline,
        state: values.state,
        companyWebsiteConfirm: values.companyWebsiteConfirm || "",
        ...consentPayload(values)
      };

      setBusy(form, true);
      setStatus(form, "", "");
      try {
        await postJson(endpoint(`/funnels/masterclass/${encodeURIComponent(sessionId)}/registrations`), payload);
        setStatus(form, "Registro completado. Revisa tu email y SMS para la confirmacion.", "ok");
        form.reset();
      } catch (error) {
        setStatus(form, "No pudimos completar el registro. Revisa los campos e intenta otra vez.", "error");
      } finally {
        setBusy(form, false);
      }
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    document.querySelectorAll("[data-default-text]").forEach((button) => {
      button.dataset.defaultText = button.textContent;
    });

    const consultationForm = document.querySelector("[data-consultation-form]");
    if (consultationForm) {
      initConsultationForm(consultationForm);
    }

    const masterclassForm = document.querySelector("[data-masterclass-form]");
    if (masterclassForm) {
      initMasterclassForm(masterclassForm);
    }
  });
})();
