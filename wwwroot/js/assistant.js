// The website assistant's chat bubble (Components/Sections/AssistantWidget.razor, Assistant/).
// The conversation lives in this tab (sessionStorage), so it survives page changes and the server keeps
// nothing; each message posts the recent turns to /assistant/message. Replies are a small, safe subset of
// Markdown, rendered as the platform's web chat does (MK.Chat .../WebChat/WebChatPage.html): escape first,
// then bold, links, lists, tables, and [[choice]] lines as quick-reply buttons.
(function () {
  const KEY = 'mk.assistant';
  const MAX_TURNS = 20;

  function load() {
    try { return JSON.parse(sessionStorage.getItem(KEY) || '{}') || {}; } catch (e) { return {}; }
  }
  function save(state) {
    try { sessionStorage.setItem(KEY, JSON.stringify(state)); } catch (e) { }
  }

  function esc(s) { return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

  // Inline: escape first, park links behind placeholders so the emphasis rules never touch a URL, then
  // bold / italic / code, then put the links back. Site paths (/coffee/pricing) open here; others in a tab.
  function inline(src) {
    const keep = []; const park = (h) => '\u0000' + (keep.push(h) - 1) + '\u0000';
    let h = esc(src);
    h = h.replace(/`([^`]+)`/g, (m, c) => park('<code>' + c + '</code>'));
    h = h.replace(/\[([^\]]+)\]\(((?:https?:\/\/|\/(?!\/))[^\s)]*)\)/g, (m, txt, u) => park(link(u, txt)));
    h = h.replace(/https?:\/\/[^\s<]+/g, (u) => {
      const tail = (u.match(/[.,;:!?)\]]+$/) || [''])[0]; if (tail) u = u.slice(0, -tail.length);
      return park(link(u, u)) + tail;
    });
    h = h.replace(/[\w.+-]+@maplekiosk\.ca/g, (m) => park('<a href="mailto:' + m + '">' + m + '</a>'));
    h = h.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>').replace(/__([^_]+)__/g, '<strong>$1</strong>');
    h = h.replace(/(^|[^*\w])\*([^*\s][^*]*)\*(?!\w)/g, '$1<em>$2</em>');
    return h.replace(/\u0000(\d+)\u0000/g, (m, i) => keep[+i]);
  }
  function link(u, txt) {
    return u.charAt(0) === '/'
      ? '<a href="' + u + '">' + txt + '</a>'
      : '<a href="' + u + '" target="_blank" rel="noopener noreferrer">' + txt + '</a>';
  }

  const CHOICE_LINE = /^\s*(\[\[[^\[\]\n]{1,80}\]\]\s*)+$/, CHOICE = /\[\[([^\[\]\n]{1,80})\]\]/g;
  function cells(line) { return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim()); }

  // A reply → { html, choices }.
  function md(src) {
    const lines = String(src || '').replace(/\r\n?/g, '\n').split('\n');
    const out = [], choices = []; let para = [];
    const flush = () => { if (para.length) { out.push('<p>' + para.map(inline).join('<br>') + '</p>'); para = []; } };
    for (let i = 0; i < lines.length; i++) {
      const L = lines[i];
      if (CHOICE_LINE.test(L)) { let m; CHOICE.lastIndex = 0; while ((m = CHOICE.exec(L))) if (choices.length < 4) choices.push(m[1].trim()); continue; }
      if (L.includes('|') && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/.test(lines[i + 1])) {
        flush(); const head = cells(L); const rows = [];
        for (i += 2; i < lines.length && lines[i].includes('|') && lines[i].trim(); i++) rows.push(cells(lines[i]));
        i--;
        let h = '<div class="ast-tbl"><table><thead><tr>' + head.map((c) => '<th>' + inline(c) + '</th>').join('') + '</tr></thead><tbody>';
        for (const r of rows) h += '<tr>' + head.map((_, k) => '<td>' + inline(r[k] || '') + '</td>').join('') + '</tr>';
        out.push(h + '</tbody></table></div>'); continue;
      }
      if (/^\s*([-*•])\s+/.test(L)) {
        flush(); const it = [];
        for (; i < lines.length && /^\s*([-*•])\s+/.test(lines[i]); i++) it.push('<li>' + inline(lines[i].replace(/^\s*([-*•])\s+/, '')) + '</li>');
        i--; out.push('<ul>' + it.join('') + '</ul>'); continue;
      }
      if (/^\s*\d+[.)]\s+/.test(L)) {
        flush(); const it = [];
        for (; i < lines.length && /^\s*\d+[.)]\s+/.test(lines[i]); i++) it.push('<li>' + inline(lines[i].replace(/^\s*\d+[.)]\s+/, '')) + '</li>');
        i--; out.push('<ol>' + it.join('') + '</ol>'); continue;
      }
      if (/^\s*#{1,6}\s+/.test(L)) { flush(); out.push('<p><strong>' + inline(L.replace(/^\s*#{1,6}\s+/, '')) + '</strong></p>'); continue; }
      if (!L.trim()) { flush(); continue; }
      para.push(L.trim());
    }
    flush();
    return { html: out.join(''), choices };
  }

  function wire() {
    const root = document.querySelector('[data-assistant]');
    if (!root || root.dataset.wired) return;
    root.dataset.wired = '1';

    const panel = root.querySelector('.ast-panel');
    const log = root.querySelector('[data-ast-log]');
    const form = root.querySelector('[data-ast-form]');
    const input = root.querySelector('[data-ast-input]');
    const starters = root.querySelector('[data-ast-starters]');
    const fab = root.querySelector('.ast-fab');
    const lang = root.dataset.lang || 'en';
    let state = load();
    state.turns = Array.isArray(state.turns) ? state.turns : [];
    let busy = false;

    function bubble(who, html) {
      const row = document.createElement('div');
      row.className = 'ast-msg ast-msg--' + who;
      const b = document.createElement('div');
      b.className = 'ast-b';
      b.innerHTML = html;
      row.appendChild(b);
      log.appendChild(row);
      return row;
    }
    function clearChoices() { log.querySelectorAll('.ast-choices').forEach((c) => c.remove()); }
    function choices(list) {
      if (!list.length) return;
      const c = document.createElement('div');
      c.className = 'ast-choices';
      list.forEach((t) => { const b = document.createElement('button'); b.type = 'button'; b.textContent = t; b.addEventListener('click', () => send(t)); c.appendChild(b); });
      log.appendChild(c);
    }
    function show(turn, withChoices) {
      if (turn.role === 'user') { bubble('me', '<p>' + esc(turn.text).replace(/\n/g, '<br>') + '</p>'); return; }
      const r = md(turn.text);
      bubble('bot', r.html || '<p>' + esc(turn.text) + '</p>');
      if (withChoices) choices(r.choices);
    }
    function bottom() { log.scrollTop = log.scrollHeight; }
    function render() {
      log.querySelectorAll('.ast-msg:not([data-ast-greeting]), .ast-choices').forEach((n) => n.remove());
      state.turns.forEach((t, i) => show(t, i === state.turns.length - 1));
      starters.hidden = state.turns.length > 0;
      bottom();
    }

    function setOpen(open) {
      panel.hidden = !open;
      root.classList.toggle('is-open', open);
      root.querySelectorAll('[data-ast-toggle]').forEach((b) => b.setAttribute('aria-expanded', String(open)));
      state.open = open; save(state);
      if (open) { bottom(); if (!matchMedia('(pointer: coarse)').matches) setTimeout(() => input.focus(), 30); }
      else fab.focus();
    }

    async function send(text) {
      text = (text || '').trim();
      if (!text || busy) return;
      busy = true;
      clearChoices();
      const history = state.turns.slice(-MAX_TURNS);
      state.turns.push({ role: 'user', text: text });
      save(state);
      show(state.turns[state.turns.length - 1]);
      starters.hidden = true;
      const typing = bubble('bot', '<span class="ast-typing" aria-label="…"><i></i><i></i><i></i></span>');
      bottom();
      let reply;
      try {
        const res = await fetch('/assistant/message', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ text: text, history: history, lang: lang, page: location.pathname }),
        });
        reply = res.ok ? (await res.json()).reply : null;
      } catch (e) { reply = null; }
      typing.remove();
      if (reply) {
        state.turns.push({ role: 'assistant', text: reply });
        state.turns = state.turns.slice(-MAX_TURNS * 2);
        save(state);
        show(state.turns[state.turns.length - 1], true);
      } else {
        bubble('bot', '<p>' + esc(root.dataset.error) + '</p>').classList.add('ast-msg--err');
      }
      busy = false;
      bottom();
    }

    root.querySelectorAll('[data-ast-toggle]').forEach((b) => b.addEventListener('click', () => setOpen(panel.hidden)));
    root.querySelector('[data-ast-reset]').addEventListener('click', () => { state.turns = []; save(state); render(); input.focus(); });
    root.querySelectorAll('[data-ast-say]').forEach((b) => b.addEventListener('click', () => send(b.textContent)));
    form.addEventListener('submit', (ev) => { ev.preventDefault(); const t = input.value; input.value = ''; grow(); send(t); });
    input.addEventListener('keydown', (ev) => { if (ev.key === 'Enter' && !ev.shiftKey && !ev.isComposing) { ev.preventDefault(); form.requestSubmit(); } });
    function grow() { input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 120) + 'px'; }
    input.addEventListener('input', grow);
    root.addEventListener('keydown', (ev) => { if (ev.key === 'Escape' && !panel.hidden) setOpen(false); });

    render();
    if (state.open) { panel.hidden = false; root.classList.add('is-open'); root.querySelectorAll('[data-ast-toggle]').forEach((b) => b.setAttribute('aria-expanded', 'true')); }
  }

  wire();
  // Blazor's enhanced navigation swaps the page without reloading the script.
  if (window.Blazor && typeof window.Blazor.addEventListener === 'function') window.Blazor.addEventListener('enhancedload', wire);
  else document.addEventListener('enhancedload', wire);
})();
