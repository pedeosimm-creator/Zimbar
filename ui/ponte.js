/* ═══════════════════════════════════════════════════════════════
   PONTE — o que o navegador não faz sozinho, o C# faz por ele:
   notas em janelas de verdade, captura de tela, OCR, abrir link no
   navegador, arrastar/minimizar a janela e buscar as notícias (o RSS
   não libera CORS). Mensagem vai por postMessage; quando precisa de
   resposta, marca com um id e espera a volta.
   ═══════════════════════════════════════════════════════════════ */
const Ponte = (() => {
  const nativo = !!(window.chrome && window.chrome.webview);
  const esperando = new Map();
  let seq = 0;

  if (nativo) {
    window.chrome.webview.addEventListener('message', ev => {
      const m = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data;
      if (m && m.resposta !== undefined && esperando.has(m.resposta)) {
        const { ok, erro } = esperando.get(m.resposta);
        esperando.delete(m.resposta);
        m.erro ? erro(new Error(m.erro)) : ok(m);
        return;
      }
      if (m && m.evento) aoReceber(m);
    });
  }

  /* eventos vindos do C# — a interface se atualiza sozinha */
  function aoReceber(m) {
    if (m.evento === 'capturado') {          // Apanhador mandou texto pra cá
      S.captura.unshift({ id: Dados.novoId(), t: m.texto });
      salvar(); renderCaptura(); go('hoje'); toast('capturado ✓');
    }
    if (m.evento === 'nota-mudou') {         // a janela da nota salvou
      const n = S.notas.find(x => x.id === m.id);
      if (n) { n.t = m.titulo; n.b = m.corpo; n.c = m.cor; }
      else S.notas.unshift({ id: m.id, t: m.titulo, b: m.corpo, c: m.cor });
      BASE = null;                            // veio do C#, não remandar
      renderBiblioteca(); atualizaContadorNotas();
      Dados.carregar().then(d => { S.notas = d.notas; BASE = retrato(); renderBiblioteca(); })
                      .catch(() => { BASE = retrato(); });
    }
    if (m.evento === 'nota-apagada') {
      S.notas = S.notas.filter(x => x.id !== m.id);
      BASE = retrato();
      renderBiblioteca(); atualizaContadorNotas();
    }
    if (m.evento === 'recarregar') recarregar();
  }

  /* dentro do aplicativo: a página vira a janela e o cabeçalho arrasta */
  if (nativo) {
    document.addEventListener('DOMContentLoaded', () => {
      document.documentElement.classList.add('nativo');
      const cab = document.querySelector('.head');
      cab.addEventListener('mousedown', e => {
        if (e.button !== 0) return;
        if (e.target.closest('input,button,textarea,select,.jbtn')) return;
        window.chrome.webview.postMessage(JSON.stringify({ acao: 'arrastar' }));
      });
      cab.addEventListener('dblclick', e => {
        if (e.target.closest('input,button,.jbtn')) return;
        window.chrome.webview.postMessage(JSON.stringify({ acao: 'maximizar' }));
      });
      const bt = (id, acao) => document.getElementById(id)
        .addEventListener('click', () => window.chrome.webview.postMessage(JSON.stringify({ acao })));
      bt('btMin', 'minimizar'); bt('btMax', 'maximizar'); bt('btFec', 'fechar');
      bordasDeRedimensionar();
    });
  }

  /* A janela não tem moldura, então as bordas de puxar são desenhadas aqui:
     oito faixas invisíveis nas beiradas que devolvem o clique pro Windows. */
  function bordasDeRedimensionar() {
    const E = 6;                                   // espessura da faixa
    const lados = [
      ['n',  'left:0;right:0;top:0;height:' + E + 'px', 'ns-resize'],
      ['s',  'left:0;right:0;bottom:0;height:' + E + 'px', 'ns-resize'],
      ['w',  'top:0;bottom:0;left:0;width:' + E + 'px', 'ew-resize'],
      ['e',  'top:0;bottom:0;right:0;width:' + E + 'px', 'ew-resize'],
      ['nw', 'left:0;top:0;width:14px;height:14px', 'nwse-resize'],
      ['ne', 'right:0;top:0;width:14px;height:14px', 'nesw-resize'],
      ['sw', 'left:0;bottom:0;width:14px;height:14px', 'nesw-resize'],
      ['se', 'right:0;bottom:0;width:14px;height:14px', 'nwse-resize']
    ];
    for (const [borda, pos, cursor] of lados) {
      const el = document.createElement('div');
      el.className = 'puxar';
      el.style.cssText = 'position:fixed;z-index:9999;' + pos + ';cursor:' + cursor;
      el.addEventListener('mousedown', e => {
        if (e.button !== 0) return;
        e.preventDefault();
        window.chrome.webview.postMessage(JSON.stringify({ acao: 'redimensionar', borda }));
      });
      document.body.appendChild(el);
    }
  }

  return {
    tem: () => nativo,
    enviar(msg) { if (nativo) window.chrome.webview.postMessage(JSON.stringify(msg)); },
    pedir(msg) {
      if (!nativo) return Promise.reject(new Error('fora do aplicativo'));
      const id = ++seq;
      return new Promise((ok, erro) => {
        esperando.set(id, { ok, erro });
        window.chrome.webview.postMessage(JSON.stringify({ ...msg, pedido: id }));
        setTimeout(() => {
          if (esperando.has(id)) { esperando.delete(id); erro(new Error('demorou demais')); }
        }, 20000);
      });
    }
  };
})();
