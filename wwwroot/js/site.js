(function () {
  function openTrialModal() {
    const m = document.getElementById('trialModal');
    if (!m) {
      // Pages without the form (legal pages, ...) send you to the homepage's.
      const seg = location.pathname.split('/')[1];
      const root = ['fr', 'vi', 'ru'].includes(seg) ? '/' + seg : '/';
      document.documentElement.classList.add('nav-busy');
      location.href = root + '#demo';
      return;
    }
    m.classList.add('open');
    document.body.classList.add('modal-open');
    // Desktop only: on a phone, focusing pops the keyboard over the modal as it opens.
    const first = m.querySelector('input, select, textarea');
    if (first && !matchMedia('(pointer: coarse)').matches) setTimeout(() => first.focus(), 50);
  }
  function closeTrialModal() {
    const m = document.getElementById('trialModal');
    if (!m) return;
    m.classList.remove('open');
    document.body.classList.remove('modal-open');
  }
  window.openTrialModal  = openTrialModal;
  window.closeTrialModal = closeTrialModal;

  function closeUserMenu() {
    const pop = document.querySelector('[data-user-pop].open');
    if (pop) pop.classList.remove('open');
    const btn = document.querySelector('[data-user-menu][aria-expanded="true"]');
    if (btn) btn.setAttribute('aria-expanded', 'false');
  }
  window.closeUserMenu = closeUserMenu;

  // ===== Video lightbox: any [data-video] opens it — a YouTube id, or a site-served
  // file path ("/media/....mp4") played in a native <video> =====
  // A film can come in several languages: data-video is the cut in data-video-lang (English when
  // absent) and data-video-<lang> adds the others. The visitor's pick is remembered; failing that
  // the page's language, then the first cut.
  const FILM_LANGS = { en: 'English', fr: 'Français', vi: 'Tiếng Việt', ru: 'Русский' };
  function isFile(id) { return /^\/|^https?:|\.mp4$/i.test(id); }
  let lastFocus = null;
  function filmLang(langs) {
    try { const v = localStorage.getItem('mk.filmLang'); if (langs.indexOf(v) >= 0) return v; } catch (e) { }
    const seg = location.pathname.split('/')[1];
    const page = FILM_LANGS[seg] ? seg : 'en';
    return langs.indexOf(page) >= 0 ? page : langs[0];
  }
  function setFilmLang(lang) {
    try { localStorage.setItem('mk.filmLang', lang); } catch (e) { }
    showFilmLang();
  }
  // Pressed state of each language group on the page, and film lengths (data-len-<lang>) in the
  // language each film will play in.
  function showFilmLang() {
    document.querySelectorAll('.film-lang').forEach((g) => {
      const btns = Array.from(g.querySelectorAll('[data-film-lang]'));
      const lang = filmLang(btns.map((b) => b.getAttribute('data-film-lang')));
      btns.forEach((b) => b.setAttribute('aria-pressed', String(b.getAttribute('data-film-lang') === lang)));
    });
    const sel = Object.keys(FILM_LANGS).map((l) => '[data-len-' + l + ']').join(',');
    document.querySelectorAll(sel).forEach((el) => {
      const langs = Object.keys(FILM_LANGS).filter((l) => el.hasAttribute('data-len-' + l));
      el.textContent = el.getAttribute('data-len-' + filmLang(langs));
    });
  }
  showFilmLang();
  onEnhancedLoad(showFilmLang);
  function openFilm(trigger) {
    const cuts = {};
    cuts[trigger.getAttribute('data-video-lang') || 'en'] = trigger.getAttribute('data-video');
    Object.keys(FILM_LANGS).forEach((l) => { const id = trigger.getAttribute('data-video-' + l); if (id) cuts[l] = id; });
    const langs = Object.keys(cuts);
    const lang = filmLang(langs);
    openVideo(cuts[lang], trigger.getAttribute('data-start'), langs.length > 1 ? { cuts: cuts, lang: lang } : null,
      trigger.hasAttribute('data-video-tall'));
  }
  // tall = a vertical (9:16) film, e.g. a YouTube Short.
  function openVideo(id, start, alt, tall) {
    closeVideo();
    lastFocus = document.activeElement;
    const lang = alt ? alt.lang : (document.documentElement.lang || 'en');
    const box = document.createElement('div');
    box.className = 'vbox';
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-modal', 'true');
    box.innerHTML =
      '<button type="button" class="vbox__close" aria-label="Close" data-close-video>&times;</button>' +
      (alt ? '<div class="vbox__lang film-lang" role="group" aria-label="Language">' +
        Object.keys(alt.cuts).map((l) => '<button type="button" lang="' + l + '" data-vbox-lang="' + l +
          '" aria-pressed="' + (alt.lang === l) + '">' + FILM_LANGS[l] + '</button>').join('') + '</div>' : '') +
      '<div class="vbox__frame' + (tall ? ' vbox__frame--tall' : '') + '">' + (isFile(id)
        ? '<video src="' + encodeURI(id) + (start ? '#t=' + (parseInt(start, 10) || 0) : '') +
          '" controls autoplay playsinline preload="auto" title="MapleKiosk video"></video>'
        : '<iframe src="https://www.youtube-nocookie.com/embed/' + encodeURIComponent(id) +
          '?autoplay=1&rel=0&modestbranding=1&hl=' + lang +
          (start ? '&start=' + (parseInt(start, 10) || 0) : '') + '" title="MapleKiosk video"' +
          ' allow="autoplay; encrypted-media; picture-in-picture; fullscreen" allowfullscreen></iframe>') + '</div>';
    box.addEventListener('click', (e) => {
      if (e.target === box) { closeVideo(); return; }
      const pick = e.target.closest('[data-vbox-lang]');
      if (pick && alt && pick.getAttribute('data-vbox-lang') !== alt.lang) {
        const keep = lastFocus;
        setFilmLang(pick.getAttribute('data-vbox-lang'));
        openVideo(alt.cuts[pick.getAttribute('data-vbox-lang')], null, { cuts: alt.cuts, lang: pick.getAttribute('data-vbox-lang') }, tall);
        lastFocus = keep;
      }
    });
    document.body.appendChild(box);
    document.body.classList.add('modal-open');
    box.querySelector('.vbox__close').focus();
  }
  function closeVideo() {
    const box = document.querySelector('.vbox');
    if (!box) return;
    box.remove();
    document.body.classList.remove('modal-open');
    if (lastFocus) { lastFocus.focus(); lastFocus = null; }
  }

  document.addEventListener('click', (ev) => {
    const vid = ev.target.closest('[data-video]');
    if (vid) { ev.preventDefault(); ev.stopPropagation(); openFilm(vid); return; }
    const langBtn = ev.target.closest('[data-film-lang]');
    if (langBtn) { ev.preventDefault(); setFilmLang(langBtn.getAttribute('data-film-lang')); return; }
    if (ev.target.closest('[data-close-video]')) { ev.preventDefault(); closeVideo(); return; }

    const menuBtn = ev.target.closest('[data-user-menu]');
    if (menuBtn) {
      ev.preventDefault(); ev.stopPropagation();
      const pop = menuBtn.parentElement.querySelector('[data-user-pop]');
      const willOpen = pop && !pop.classList.contains('open');
      closeUserMenu();
      if (willOpen) { pop.classList.add('open'); menuBtn.setAttribute('aria-expanded', 'true'); }
      return;
    }
    // A click anywhere outside the open popup closes it (clicks inside it pass through).
    if (!ev.target.closest('[data-user-pop]')) closeUserMenu();

    const shToggle = ev.target.closest('[data-sh-toggle]');
    if (shToggle) { toggleShopItem(shToggle); return; }

    if (ev.target.closest('[data-open-trial]'))  { ev.preventDefault(); ev.stopPropagation(); openTrialModal();  return; }
    if (ev.target.closest('[data-close-trial]')) { ev.preventDefault(); ev.stopPropagation(); closeTrialModal(); return; }

    const a = ev.target.closest('a[href*="#"]');
    if (!a) return;
    const href = a.getAttribute('href');
    const cut = href.indexOf('#');
    const path = href.slice(0, cut);
    const hash = href.slice(cut);
    // Smooth-scroll only same-page anchors; others go to Blazor navigation.
    const norm = (p) => p.replace(/\/+$/, '') || '/';
    if (path && norm(path) !== norm(location.pathname)) return;
    if (!hash || hash === '#') return;
    const el = document.querySelector(hash);
    if (!el) return;
    ev.preventDefault();
    ev.stopPropagation();
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, true);

  document.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape') { closeVideo(); closeTrialModal(); closeUserMenu(); }
  });

  // Arriving from another page's "Book a demo" (see openTrialModal).
  if (location.hash === '#demo') {
    history.replaceState(null, '', location.pathname + location.search);
    setTimeout(openTrialModal, 300);
  }

  const nav = document.querySelector('.nav');
  let lastY = 0;
  function onScroll() {
    const y = window.scrollY;
    if (!nav) return;
    nav.classList.toggle('scrolled', y > 8);
    if (y > lastY && y > 200) nav.classList.add('hidden');
    else nav.classList.remove('hidden');
    lastY = y;
  }
  window.addEventListener('scroll', onScroll, { passive: true });

  if ('IntersectionObserver' in window) {
    document.documentElement.classList.add('fade-ready');
    const io = new IntersectionObserver((entries) => {
      entries.forEach(e => {
        if (e.isIntersecting) { e.target.classList.add('visible'); io.unobserve(e.target); }
      });
    }, { threshold: 0.08, rootMargin: '0px 0px -5% 0px' });
    document.querySelectorAll('.fade-in').forEach(el => io.observe(el));
  }

  document.querySelectorAll('img[data-fallback]').forEach(img => {
    img.addEventListener('error', () => {
      const label = img.getAttribute('data-fallback') || 'Image';
      const div = document.createElement('div');
      div.className = 'img-fallback';
      div.style.cssText = 'width:100%;height:100%;min-height:' + (img.height || 260) + 'px';
      div.textContent = label;
      img.replaceWith(div);
    });
  });

  // ===== Email assembly (defeats Cloudflare "[email protected]" obfuscation) =====
  // The server HTML never contains a literal user@domain string, so Cloudflare has
  // nothing to rewrite. We build the mailto link + visible text client-side instead.
  function wireMail() {
    document.querySelectorAll('a[data-user][data-domain]').forEach(a => {
      if (a.dataset.mailWired) return;
      a.dataset.mailWired = '1';
      const addr = a.getAttribute('data-user') + '@' + a.getAttribute('data-domain');
      a.setAttribute('href', 'mailto:' + addr);
      a.textContent = addr;
    });
  }
  wireMail();
  // Re-run after Blazor enhanced navigation swaps the DOM.
  onEnhancedLoad(wireMail);

  // ===== Quote builder (/nails/pricing) =====
  // Each option is an input with data-price and data-period ("once" or "month"). Whatever is
  // ticked becomes a bill line; the totals are the one-time setup, the monthly bill, and the
  // first payment (setup plus the first month). Amounts are formatted in the page's locale.
  // The same lines fill the printed quote ([data-quote-doc]) with the customer's details, which
  // this browser remembers for next time.
  // A language switch keeps the builder's elements (and their listeners) but drops the quoteWired marker, so
  // it gets wired again: drop the previous wiring first, or Print and Buy fire once per switch (one Stripe
  // checkout each).
  let quoteWiring = null;
  function wireQuote() {
    const root = document.querySelector('[data-quote]');
    if (!root || root.dataset.quoteWired) return;
    root.dataset.quoteWired = '1';
    if (quoteWiring) quoteWiring.abort();
    quoteWiring = new AbortController();
    const once = { signal: quoteWiring.signal };
    const locale = root.dataset.locale || 'en-US';
    const fmt = new Intl.NumberFormat(locale, { style: 'currency', currency: 'CAD', currencyDisplay: 'narrowSymbol' });
    const money = (n) => fmt.format(Math.round(n * 100) / 100);
    const set = (sel, text) => document.querySelectorAll(sel).forEach((el) => { el.textContent = text; });
    const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
    root.querySelectorAll('[data-money]').forEach((e) => { e.textContent = money(+e.dataset.money); });

    const doc = document.querySelector('[data-quote-doc]');
    const docSet = (k, v) => { const e = doc && doc.querySelector('[data-doc="' + k + '"]'); if (e) e.textContent = v; };
    const day = (d) => d.toLocaleDateString(locale, { year: 'numeric', month: 'long', day: 'numeric' });
    const now = new Date();
    const pad = (n) => String(n).padStart(2, '0');
    docSet('no', 'Q-' + now.getFullYear() + pad(now.getMonth() + 1) + pad(now.getDate()) + '-' + String(Math.floor(Math.random() * 9000) + 1000));
    docSet('date', day(now));
    docSet('valid', day(new Date(now.getTime() + 30 * 864e5)));
    document.querySelectorAll('[data-mail-text][data-domain]').forEach((e) => { e.textContent = e.dataset.mailText + '@' + e.dataset.domain; });

    // Customer details: typed in the bill, printed on the quote. Blank prints as a line to write on.
    const cust = Array.from(root.querySelectorAll('[data-cust]'));
    let saved = {};
    try { saved = JSON.parse(localStorage.getItem('mk.quoteCustomer') || '{}') || {}; } catch (e) { }
    cust.forEach((i) => { if (saved[i.dataset.cust]) i.value = saved[i.dataset.cust]; });
    function customer() {
      const v = {};
      cust.forEach((i) => { v[i.dataset.cust] = i.value.trim(); docSet(i.dataset.cust, v[i.dataset.cust]); });
      try { localStorage.setItem('mk.quoteCustomer', JSON.stringify(v)); } catch (e) { }
    }
    root.addEventListener('input', (ev) => { if (ev.target.matches('[data-cust]')) customer(); }, once);
    customer();

    const lines = root.querySelector('[data-quote-lines]');
    function update() {
      let once = 0, month = 0;
      const picked = [];
      lines.replaceChildren();
      // Monthly items make the monthly bill; yearly ones are listed per year; all of it is due first.
      root.querySelectorAll('input[data-price]:checked').forEach((i) => {
        const price = +i.dataset.price, period = i.dataset.period, monthly = period !== 'once';
        const per = period === 'month' ? ' ' + root.dataset.perMonth : period === 'year' ? ' ' + root.dataset.perYear : '';
        if (period === 'month') month += price; else once += price;
        picked.push({ name: i.dataset.name, detail: i.dataset.detail || '', price: price, monthly: monthly, period: period, per: per });
        const li = el('li');
        li.append(el('span', null, i.dataset.name), el('span', null, money(price) + per));
        lines.append(li);
      });
      if (!picked.length) lines.append(el('li', 'pr-bill__empty', root.dataset.empty));
      set('[data-quote-once]', money(once));
      set('[data-quote-month]', money(month));
      set('[data-quote-first]', money(once + month));

      if (!doc) return;
      const body = doc.querySelector('[data-doc="lines"]');
      body.replaceChildren();
      let n = 0;
      [[false, doc.dataset.gOnce], [true, doc.dataset.gMonth]].forEach(([monthly, title]) => {
        const rows = picked.filter((p) => p.monthly === monthly);
        if (!rows.length) return;
        const g = el('tr', 'pr-doc__group');
        const th = el('th', null, title); th.colSpan = 4; g.append(th);
        body.append(g);
        rows.forEach((p) => {
          const tr = el('tr');
          const d = el('td');
          d.append(el('strong', null, p.name));
          if (p.detail) d.append(el('small', null, p.detail));
          tr.append(el('td', 'pr-doc__n', String(++n)), d,
            el('td', null, p.period === 'month' ? doc.dataset.monthly : p.period === 'year' ? root.dataset.perYear : doc.dataset.once),
            el('td', 'pr-doc__amt', money(p.price) + p.per));
          body.append(tr);
        });
      });
      if (!picked.length) {
        const tr = el('tr'); const td = el('td', 'pr-doc__none', doc.dataset.empty); td.colSpan = 4; tr.append(td); body.append(tr);
      }
      docSet('once', money(once));
      docSet('month', money(month) + ' ' + root.dataset.perMonth);
      docSet('first', money(once + month));
    }
    // The picks are remembered per product, so coming back from Stripe without paying (or later)
    // finds the same quote.
    const buy = root.querySelector('[data-quote-buy-wrap]');
    const picksKey = 'mk.quoteItems.' + (buy ? buy.dataset.product : '');
    try {
      const was = JSON.parse(localStorage.getItem(picksKey) || 'null');
      if (Array.isArray(was)) root.querySelectorAll('input[data-price]').forEach((i) => { i.checked = was.includes(i.value); });
      // A pick-one group with nothing remembered falls back to its "Not now".
      root.querySelectorAll('.pr-none input').forEach((n) => {
        n.checked = !root.querySelector('input[name="' + n.name + '"][data-price]:checked');
      });
    } catch (e) { }
    // What's ticked, as catalog SKUs.
    const picks = () => Array.from(root.querySelectorAll('input[data-price]:checked')).map((i) => i.value);
    root.addEventListener('change', () => { try { localStorage.setItem(picksKey, JSON.stringify(picks())); } catch (e) { } }, once);

    root.addEventListener('change', update, once);
    update();

    const print = root.querySelector('[data-quote-print]');
    if (print) print.addEventListener('click', () => window.print(), once);

    // Buy online: send the picks (never prices; the server prices them from the catalog) and go to
    // Stripe. Setup is paid once; monthly plans become a subscription.
    if (buy) {
      const btn = buy.querySelector('[data-quote-buy]');
      const msg = buy.querySelector('[data-quote-msg]');
      const say = (t) => { msg.textContent = t || ''; };
      btn.addEventListener('click', async () => {
        const p = picks();
        if (!p.length) { say(buy.dataset.need); return; }
        const v = {};
        cust.forEach((i) => { v[i.dataset.cust] = i.value.trim(); });
        const email = root.querySelector('[data-cust="email"]');
        if (!v.email || (email && !email.checkValidity())) { say(buy.dataset.email); if (email) email.focus(); return; }
        btn.disabled = true; say(buy.dataset.wait);
        try {
          const r = await fetch('/api/checkout/quote', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ product: buy.dataset.product, culture: buy.dataset.culture, items: p,
              company: v.salon, name: v.name, phone: v.phone, email: v.email })
          });
          const res = await r.json().catch(() => ({}));
          if (r.ok && res.stripeUrl) { location.href = res.stripeUrl; return; }
        } catch (e) { }
        btn.disabled = false; say(buy.dataset.fail);
      }, once);
    }

    // The phone bar is for getting to the bill; hide it once the bill is on screen.
    const bar = document.querySelector('[data-quote-bar]');
    const bill = root.querySelector('.pr-bill');
    if (bar && bill && 'IntersectionObserver' in window) {
      const io = new IntersectionObserver((es) => bar.classList.toggle('is-hidden', es[0].isIntersecting));
      io.observe(bill);
      quoteWiring.signal.addEventListener('abort', () => io.disconnect());
    }
  }
  wireQuote();
  onEnhancedLoad(wireQuote);

  // ===== Shop (/shop): the cart lives in the browser =====
  // Adding, quantities and totals answer at once; only Checkout goes to the server (/api/checkout/shop),
  // which prices the SKUs again and opens Stripe. Saved under the same key and format as MapleShop.UI's
  // CartState, so /shop/success empties it. The page itself is the catalog: each [data-shop-add] button
  // carries its item (name in the page's language, price, cadence, category, pick-one).
  const CART_KEY = 'mapleshop.cart', CART_MAX = 99;
  const shop = { msg: null, busy: false };
  function loadCart() {
    try { const v = JSON.parse(localStorage.getItem(CART_KEY) || '[]'); return Array.isArray(v) ? v : []; } catch (e) { return []; }
  }
  function saveCart(lines) {
    try { if (lines.length) localStorage.setItem(CART_KEY, JSON.stringify(lines)); else localStorage.removeItem(CART_KEY); } catch (e) { }
  }
  function shopItem(b) {
    return { Sku: b.dataset.shopAdd, Name: b.dataset.name, Price: +b.dataset.price, BillingInterval: b.dataset.interval,
             TrialDays: +b.dataset.trial || 0, Quantity: 1, Group: b.dataset.group };
  }
  const isPlan = (l) => l.BillingInterval === 'Monthly' || l.BillingInterval === 'Yearly';

  function wireShop() {
    const root = document.querySelector('[data-shop]');
    if (!root || root.dataset.shopWired) return;
    root.dataset.shopWired = '1';
    // Saved lines take today's names, prices and cadence from the page; anything no longer sold drops out.
    const buttons = {};
    document.querySelectorAll('[data-shop-add]').forEach((b) => { buttons[b.dataset.shopAdd] = b; });
    const lines = loadCart().filter((l) => l && buttons[l.Sku])
      .map((l) => Object.assign(shopItem(buttons[l.Sku]), { Quantity: Math.min(CART_MAX, Math.max(1, l.Quantity | 0)) }));
    saveCart(lines);

    // The checkout form is built once, so typing in it survives every re-render of the lines.
    const t = root.dataset;
    const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
    const body = root.querySelector('[data-shop-body]');
    const form = el('div', 'sh-cart__form');
    const label = el('label');
    const email = el('input');
    Object.assign(email, { type: 'email', autocomplete: 'email', maxLength: 120, placeholder: 'you@example.com' });
    email.setAttribute('data-shop-email', '');
    try { email.value = (JSON.parse(localStorage.getItem('mk.quoteCustomer') || '{}') || {}).email || ''; } catch (e) { }
    label.append(el('span', null, t.tEmail), email);
    const emailBox = el('div', 'sh-cart__email'); emailBox.append(label);
    const go = el('button', 'btn btn-primary', t.tCheckout);
    go.type = 'button'; go.setAttribute('data-shop-checkout', '');
    const actions = el('div', 'sh-cart__actions'); actions.append(go, el('p', 'sh-cart__fine', '🔒 ' + t.tSecure));
    form.append(emailBox, actions);
    body.replaceChildren(el('div', null), form, el('p', 'sh-cart__msg'));
    shop.msg = /[?&]checkout=cancelled/.test(location.search) ? t.tCancelled : null;
    shop.busy = false;
    renderShop(lines);
  }

  function renderShop(lines) {
    const root = document.querySelector('[data-shop]');
    if (!root) return;
    const t = root.dataset;
    const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
    const money = (n) => new Intl.NumberFormat(t.locale, { style: 'currency', currency: 'CAD', currencyDisplay: 'narrowSymbol',
      minimumFractionDigits: Number.isInteger(n) ? 0 : 2, maximumFractionDigits: 2 }).format(Math.round(n * 100) / 100);
    const per = (l) => l.BillingInterval === 'Monthly' ? ' ' + t.tMonth : l.BillingInterval === 'Yearly' ? ' ' + t.tYear : '';
    const fill = (s, v) => s.replace('{0}', v);

    // The item buttons: "Added" (hover: "Remove") when in the cart.
    const inCart = new Set(lines.map((l) => l.Sku));
    document.querySelectorAll('[data-shop-add]').forEach((b) => {
      const on = inCart.has(b.dataset.shopAdd);
      if (b.classList.contains('sh-add--in') === on && b.dataset.drawn) return;
      b.dataset.drawn = '1';
      b.classList.toggle('sh-add--in', on);
      b.setAttribute('aria-pressed', String(on));
      if (on) b.replaceChildren(el('span', 'sh-add__label', '✓ ' + t.tAdded), el('span', 'sh-add__undo', t.tRemove));
      else b.textContent = b.dataset.pickone && b.dataset.recurring ? t.tChoose : t.tAdd;
    });

    const plans = lines.filter(isPlan);
    const trial = plans.length && plans.every((l) => l.TrialDays > 0) ? Math.min(...plans.map((l) => l.TrialDays)) : 0;
    const due = lines.filter((l) => !isPlan(l) || !trial).reduce((s, l) => s + l.Price * l.Quantity, 0);
    const renews = plans.reduce((s, l) => s + l.Price * l.Quantity, 0);
    const mixed = new Set(plans.map((l) => l.BillingInterval)).size > 1;
    const count = lines.reduce((s, l) => s + l.Quantity, 0);

    const body = root.querySelector('[data-shop-body]');
    const [list, form, msg] = body.children;
    list.replaceChildren();
    if (!lines.length) list.append(el('p', 'shop-cart__empty', t.tEmpty));
    else {
      const ul = el('ul', 'shop-cart__lines');
      lines.forEach((l) => {
        const li = el('li', 'shop-cart__line');
        const row = el('div', 'shop-cart__row');
        const amt = el('span', 'shop-cart__amt', money(l.Price * l.Quantity)); amt.append(el('small', null, per(l)));
        row.append(el('span', 'shop-cart__name', l.Name), amt);
        const tools = el('div', 'shop-cart__row shop-cart__row--tools');
        const qty = el('span', 'shop-qty');
        const less = el('button', null, '−'), more = el('button', null, '+');
        [[less, -1, t.tLess], [more, 1, t.tMore]].forEach(([b, d, name]) => {
          b.type = 'button'; b.setAttribute('aria-label', name); b.dataset.shopQty = l.Sku; b.dataset.d = d;
        });
        more.disabled = l.Quantity >= CART_MAX;
        qty.append(less, el('span', null, String(l.Quantity)), more);
        tools.append(qty);
        if (l.Quantity > 1) tools.append(el('small', 'shop-cart__each', fill(t.tEach, money(l.Price))));
        const rm = el('button', 'shop-cart__remove', t.tRemove); rm.type = 'button'; rm.dataset.shopRemove = l.Sku;
        tools.append(rm);
        li.append(row, tools);
        ul.append(li);
      });
      const dl = el('dl', 'sh-cart__totals');
      const pair = (cls, dt, dd, small) => { const d = el('div', cls); const v = el('dd', null, dd); if (small) v.append(el('small', null, small)); d.append(el('dt', null, dt), v); dl.append(d); };
      if (plans.length) pair(null, trial ? fill(t.tAftertrial, trial) : t.tThen, money(renews), per(plans[0]));
      pair('sh-cart__due', t.tToday, money(due));
      list.append(ul, dl);
      if (plans.length) list.append(el('p', 'sh-cart__fine', t.tRenew));
      if (mixed) list.append(el('p', 'sh-cart__msg', t.tMixed));
    }
    form.hidden = !lines.length;
    const go = form.querySelector('[data-shop-checkout]');
    go.disabled = shop.busy || mixed;
    go.textContent = shop.busy ? t.tWait : t.tCheckout;
    msg.textContent = shop.msg || '';
    msg.hidden = !shop.msg;

    // Phones: a bar at the bottom keeps the cart in reach.
    const bar = document.querySelector('[data-shop-bar]');
    if (bar) {
      bar.hidden = !lines.length;
      const go2 = el('span', 'sh-bar__go', t.tView + ' '); go2.append(el('span', null, '↓'));
      const today = el('span', null, t.tToday + ' '); today.append(el('strong', null, money(due)));
      bar.replaceChildren(el('span', null, fill(t.tItems, count)), today, go2);
    }
  }

  function changeCart(fn) {
    const lines = loadCart();
    fn(lines);
    saveCart(lines);
    shop.msg = null;
    renderShop(lines);
  }

  async function shopCheckout() {
    const root = document.querySelector('[data-shop]');
    const lines = loadCart();
    if (!root || !lines.length || shop.busy) return;
    const input = root.querySelector('[data-shop-email]');
    const email = (input ? input.value : '').trim();
    if (!email || email.indexOf('@') < 1 || /\s/.test(email) || email.length > 120) {
      shop.msg = root.dataset.tNeedemail; renderShop(lines); if (input) input.focus(); return;
    }
    shop.busy = true; shop.msg = null; renderShop(lines);
    try {
      const r = await fetch(new URL('api/checkout/shop', document.baseURI), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ items: lines.map((l) => ({ sku: l.Sku, quantity: l.Quantity })), email: email, culture: root.dataset.culture })
      });
      const res = await r.json().catch(() => null);
      if (r.ok && res && res.stripeUrl) { location.href = res.stripeUrl; return; }
      shop.msg = (res && res.error) || root.dataset.tFail;
    } catch (e) { shop.msg = root.dataset.tFail; }
    shop.busy = false;
    renderShop(loadCart());
  }

  document.addEventListener('click', (ev) => {
    if (!document.querySelector('[data-shop]')) return;
    const add = ev.target.closest('[data-shop-add]');
    if (add) {
      const item = shopItem(add);
      changeCart((lines) => {
        const at = lines.findIndex((l) => l.Sku === item.Sku);
        if (at >= 0) { lines.splice(at, 1); return; }           // pressing "Added" takes it out again
        if (add.dataset.pickone) for (let i = lines.length - 1; i >= 0; i--) if (lines[i].Group === item.Group) lines.splice(i, 1);
        lines.push(item);
      });
      return;
    }
    const q = ev.target.closest('[data-shop-qty]');
    if (q) {
      changeCart((lines) => {
        const at = lines.findIndex((l) => l.Sku === q.dataset.shopQty);
        if (at < 0) return;
        const n = lines[at].Quantity + (+q.dataset.d);
        if (n <= 0) lines.splice(at, 1); else lines[at].Quantity = Math.min(CART_MAX, n);
      });
      return;
    }
    const rm = ev.target.closest('[data-shop-remove]');
    if (rm) { changeCart((lines) => { const at = lines.findIndex((l) => l.Sku === rm.dataset.shopRemove); if (at >= 0) lines.splice(at, 1); }); return; }
    if (ev.target.closest('[data-shop-checkout]')) shopCheckout();
  });
  // Another tab changed the cart: show it here too.
  window.addEventListener('storage', (ev) => { if (ev.key === CART_KEY && document.querySelector('[data-shop]')) renderShop(loadCart()); });
  wireShop();
  onEnhancedLoad(wireShop);

  // ===== Order from a product page (e.g. /gift-cards): into the shop's cart, then to /shop =====
  // The link carries the item like a [data-shop-add] button; /shop re-reads names and prices from its own page.
  document.addEventListener('click', (ev) => {
    const go = ev.target.closest('[data-cart-go]');
    if (!go) return;
    const d = go.dataset;
    const item = { Sku: d.cartGo, Name: d.name, Price: +d.price, BillingInterval: d.interval, TrialDays: 0, Quantity: 1, Group: d.group };
    const lines = loadCart().filter((l) => l && l.Sku !== item.Sku && !(d.pickone && l.Group === item.Group));
    lines.push(item);
    saveCart(lines);
    // The link's own href (/shop#cart) does the rest.
  });

  // ===== Shop (/shop): a row opens in place to show what's included =====
  function toggleShopItem(btn, open) {
    const more = document.getElementById(btn.getAttribute('aria-controls'));
    if (!more) return;
    const show = open ?? btn.getAttribute('aria-expanded') !== 'true';
    btn.setAttribute('aria-expanded', String(show));
    more.hidden = !show;
  }
  function openShopItemFromHash() {
    if (!location.hash.startsWith('#item-')) return;
    const row = document.getElementById(decodeURIComponent(location.hash.slice(1)));
    const btn = row && row.querySelector('[data-sh-toggle]');
    if (!btn) return;
    toggleShopItem(btn, true);
    row.scrollIntoView({ block: 'center' });
  }
  openShopItemFromHash();
  onEnhancedLoad(openShopItemFromHash);

  // Blazor raises 'enhancedload' through Blazor.addEventListener (not as a DOM event).
  function onEnhancedLoad(fn) {
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') window.Blazor.addEventListener('enhancedload', fn);
    else document.addEventListener('enhancedload', fn);
  }

  // ===== Page changes feel instant =====
  // Every page is a round trip to the origin (Cloudflare doesn't cache HTML), and enhanced navigation
  // keeps the old page on screen until the new one arrives — so a click looked ignored. Show a
  // loading bar the moment a link is clicked, and prefetch pages on hover/touch so the click is
  // usually served from the browser cache (the server lets signed-out visitors cache pages briefly).
  function sameSitePage(a) {
    if (!a || a.target || a.hasAttribute('download') || a.hasAttribute('data-video')) return null;
    const href = a.getAttribute('href');
    if (!href || href.startsWith('#') || href.startsWith('javascript:') || href.startsWith('mailto:')) return null;
    const url = new URL(a.href, location.href);
    if (url.origin !== location.origin || /^\/(api|media|login|logout|auth|signin|onboarding)\b/.test(url.pathname)) return null;
    if (url.pathname === location.pathname && url.search === location.search) return null;
    return url;
  }

  const prefetched = new Set();
  function prefetch(ev) {
    const url = sameSitePage(ev.target.closest && ev.target.closest('a[href]'));
    if (!url || prefetched.has(url.href)) return;
    prefetched.add(url.href);
    fetch(url.href, { credentials: 'same-origin', headers: { 'Accept': 'text/html' } }).catch(() => prefetched.delete(url.href));
  }
  document.addEventListener('pointerover', prefetch, { passive: true });
  document.addEventListener('touchstart', prefetch, { passive: true });
  document.addEventListener('focusin', prefetch);

  let busyTimer = null;
  function navDone() {
    document.documentElement.classList.remove('nav-busy');
    document.querySelectorAll('.nav-pending').forEach(x => x.classList.remove('nav-pending'));
    clearTimeout(busyTimer);
  }
  document.addEventListener('click', (ev) => {
    // (Not checking defaultPrevented: Blazor's enhanced navigation prevents the default first.)
    if (ev.button !== 0 || ev.metaKey || ev.ctrlKey || ev.shiftKey || ev.altKey) return;
    const a = ev.target.closest && ev.target.closest('a[href]');
    if (!sameSitePage(a)) return;
    // Language pills: light up the chosen one right away.
    const sw = a.closest('.lang-switch');
    if (sw) sw.querySelectorAll('a').forEach(x => x.classList.toggle('active', x === a));
    a.classList.add('nav-pending');
    document.documentElement.classList.add('nav-busy');
    clearTimeout(busyTimer);
    busyTimer = setTimeout(navDone, 15000); // never leave the bar stuck
  }, true); // capture: Blazor's enhanced-navigation handler stops the click before the bubble phase
  onEnhancedLoad(navDone);

  // Forms (the demo request) post as enhanced forms: show the bar and lock the button meanwhile.
  document.addEventListener('submit', (ev) => {
    const btn = ev.target.querySelector('button[type="submit"][data-busy-label]');
    if (btn) { btn.disabled = true; btn.textContent = btn.getAttribute('data-busy-label'); }
    document.documentElement.classList.add('nav-busy');
    clearTimeout(busyTimer);
    busyTimer = setTimeout(navDone, 15000);
  });
  // A demo request that failed validation comes back with the modal still open.
  onEnhancedLoad(() => document.body.classList.toggle('modal-open', !!document.querySelector('#trialModal.open')));
  window.addEventListener('pageshow', navDone);

  // Clicks that came in before this script loaded (App.razor's early click catcher).
  window.mkReady = true;
  if (window.mkPendingTrial) { window.mkPendingTrial = false; navDone(); openTrialModal(); }

  // ===== Scroll to top on page change =====
  const origPushState = history.pushState.bind(history);
  history.pushState = function (state, title, url) {
    const from = location.pathname;
    origPushState(state, title, url);
    if (location.pathname !== from && !location.hash) {
      try { window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); }
      catch (e) { window.scrollTo(0, 0); }
    }
  };
})();
