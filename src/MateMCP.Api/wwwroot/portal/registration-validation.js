(() => {
  const form = document.querySelector("form.auth-form");
  const password = document.getElementById("password");
  const confirmation = document.getElementById("confirm-password");
  const message = document.getElementById("confirm-password-error");
  const submit = form?.querySelector("button[type=submit]");

  if (!form || !password || !confirmation || !message || !submit) return;

  const validate = () => {
    const hasPassword = password.value.length > 0;
    const hasConfirmation = confirmation.value.length > 0;
    const mismatch = hasPassword && hasConfirmation && password.value !== confirmation.value;

    confirmation.setCustomValidity(mismatch ? "Passwords do not match." : "");

    if (mismatch) {
      confirmation.setAttribute("aria-invalid", "true");
      message.textContent = "Passwords do not match.";
      message.hidden = false;
    } else {
      confirmation.removeAttribute("aria-invalid");
      message.hidden = true;
    }

    submit.disabled = !hasPassword || !hasConfirmation || mismatch;
    return !submit.disabled;
  };

  if (confirmation.dataset.serverError === "true") {
    submit.disabled = true;
  } else {
    validate();
  }

  password.addEventListener("input", validate);
  confirmation.addEventListener("input", validate);
  form.addEventListener("submit", event => {
    if (!validate()) {
      event.preventDefault();
      confirmation.focus();
    }
  });
})();
