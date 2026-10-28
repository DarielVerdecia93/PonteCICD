// Ajudas do painel (Blazor).
window.ponte = {
  // Pedido a API feito pelo navegador: o login, a configuracao inicial e a troca de palavra-passe criam a cookie
  // de sessao (HttpOnly), e isso so acontece numa resposta HTTP ao navegador, nao pela ligacao do Blazor.
  send: async (method, url, body) => {
    try {
      const r = await fetch(url, { method, headers: { 'Content-Type': 'application/json' }, body: body == null ? undefined : JSON.stringify(body) });
      let text = await r.text();
      if (text.startsWith('"')) { try { text = JSON.parse(text); } catch { /* texto simples */ } }
      return { ok: r.ok, status: r.status, text: r.ok ? '' : (text || r.statusText) };
    } catch {
      return { ok: false, status: 0, text: 'Sem ligação ao serviço.' };
    }
  },
  // A cookie de sessao e HttpOnly: so o servidor a apaga.
  logout: () => fetch('/api/auth/logout', { method: 'POST' }).finally(() => { location.href = '/login'; }),
  atBottom: id => {
    const el = document.getElementById(id);
    return !el || el.scrollTop + el.clientHeight >= el.scrollHeight - 20;
  },
  toBottom: id => {
    const el = document.getElementById(id);
    if (el) el.scrollTop = el.scrollHeight;
  },
};

// Ligacao perdida: com um aviso proprio (#components-reconnect-modal) o Blazor so poe classes; o resto e daqui.
// - rejected / resume-failed / paused: o servico ja nao conhece esta pagina (foi reiniciado). Recarrega: a sessao
//   continua valida (as chaves da cookie ficam em disco).
// - failed: desistiu depois de varias tentativas (servico parado muito tempo). Tenta de novo a cada 10 s.
(() => {
  const modal = document.getElementById('components-reconnect-modal');
  let retry = null;
  const stopRetry = () => { clearInterval(retry); retry = null; };
  modal?.addEventListener('components-reconnect-state-changed', ev => {
    const state = ev.detail.state;
    if (state === 'rejected' || state === 'resume-failed' || state === 'paused') location.reload();
    else if (state === 'hide') stopRetry();
    else if (state === 'failed' && !retry) {
      retry = setInterval(async () => {
        try { if (!await Blazor.reconnect()) location.reload(); else stopRetry(); } catch { /* continua sem ligacao */ }
      }, 10000);
    }
  });
})();
