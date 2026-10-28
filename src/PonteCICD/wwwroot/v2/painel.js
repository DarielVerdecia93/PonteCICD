// Ajudas do painel Blazor (/v2).
window.ponte = {
  // A cookie de sessao e HttpOnly: so o servidor a apaga. Depois volta ao login do painel classico.
  logout: () => fetch('/api/auth/logout', { method: 'POST' }).finally(() => { location.href = '/'; }),
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
