(() => {
  const measurementId = "G-QH99PFGFJD";
  const trackedHosts = new Set(["matemcp.com", "www.matemcp.com", "api.matemcp.com"]);

  if (!trackedHosts.has(window.location.hostname))
    return;

  window.dataLayer = window.dataLayer || [];
  window.gtag = function () { window.dataLayer.push(arguments); };
  window.gtag("js", new Date());
  window.gtag("config", measurementId);

  const script = document.createElement("script");
  script.async = true;
  script.src = "https://www.googletagmanager.com/gtag/js?id=" + encodeURIComponent(measurementId);
  document.head.appendChild(script);
})();
