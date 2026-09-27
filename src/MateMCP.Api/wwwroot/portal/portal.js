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
  if (window.innerWidth > 780) setPortalMenu(false);
});
