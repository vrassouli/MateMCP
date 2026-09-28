const toggle = document.querySelector('[data-portal-nav-toggle]');
const nav = document.querySelector('[data-portal-nav]');

function setPortalMenu(open) {
  if (!toggle || !nav) return;
  toggle.setAttribute('aria-expanded', String(open));
  toggle.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
  nav.classList.toggle('is-open', open);
}

toggle?.addEventListener('click', () => {
  setPortalMenu(toggle.getAttribute('aria-expanded') !== 'true');
});

nav?.querySelectorAll('a').forEach(link => link.addEventListener('click', () => setPortalMenu(false)));

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') setPortalMenu(false);
});

document.addEventListener('click', event => {
  if (!nav?.classList.contains('is-open') || !toggle) return;
  if (!nav.contains(event.target) && !toggle.contains(event.target)) setPortalMenu(false);
});

window.addEventListener('resize', () => {
  if (window.innerWidth > 820) setPortalMenu(false);
});

async function copyPortalValue(button) {
  const value = button.dataset.copyValue;
  if (!value) return;

  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
    } else {
      const textarea = document.createElement('textarea');
      textarea.value = value;
      textarea.setAttribute('readonly', '');
      textarea.style.position = 'fixed';
      textarea.style.opacity = '0';
      document.body.appendChild(textarea);
      textarea.select();
      document.execCommand('copy');
      textarea.remove();
    }

    const original = button.textContent;
    button.textContent = original?.trim() === 'Copy' ? 'Copied' : '✓';
    button.setAttribute('aria-label', 'Copied MCP endpoint');
    window.setTimeout(() => {
      button.textContent = original;
      button.setAttribute('aria-label', original?.trim() === 'Copy' ? 'Copy MCP endpoint' : 'Copy MCP endpoint');
    }, 1400);
  } catch {
    button.setAttribute('aria-label', 'Could not copy MCP endpoint');
  }
}

document.querySelectorAll('[data-copy-value]').forEach(button => {
  button.addEventListener('click', () => copyPortalValue(button));
});
