/* ═══════════════════════════════════════════════════════════════
   ZimNotes SOLTO — a MESMA biblioteca do painel embutido no Zimbar,
   numa janela própria. Renderiza os cards e os chips com a marcação
   idêntica à do renderBiblioteca de app.js, então é igual, não parecido.

   Fala direto com o Supabase (flowspace, tabela `notas`) pra listar; abrir
   uma nota é com o C# (StickyWindow), via postMessage. A janela é sem
   moldura: arrastar/redimensionar também vão pro C#.
   ═══════════════════════════════════════════════════════════════ */
const BANCO = {
  url: 'https://fautswjwioiviqvpgsrw.supabase.co',
  key: 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImZhdXRzd2p3aW9pdmlxdnBnc3J3Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzk4ODkxNzgsImV4cCI6MjA5NTQ2NTE3OH0.99cWW-JA8-fH_wj_tqNLdpgubhM98UleH-0D5YaENM4'
};

/* cores iguais às do dados.js do Zimbar */
const CORES_NOTA = [
  { k: '', hex: '#FFF7A8' }, { k: 'uva', hex: '#E4D5FF' }, { k: 'vinho', hex: '#FFD0E8' },
  { k: 'mel', hex: '#FFE3A8' }, { k: 'mata', hex: '#CFF5DC' }, { k: 'noite', hex: '#D6EBFF' }
];
const corHex = (k) => (CORES_NOTA.find(c => c.k === (k || '')) || CORES_NOTA[0]).hex;
const CORES_LISTA = ['#3b7bd4', '#c9a227', '#2f7d55', '#d4568a', '#7b5cd6', '#c2622d'];
function corLista(nome) {
  let h = 0; for (const ch of String(nome)) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return CORES_LISTA[h % CORES_LISTA.length];
}
const esc = (s) => String(s ?? '').replace(/[<>&]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' }[c]));
const semTags = (s) => String(s || '').replace(/<[^>]*>/g, '');

let NOTAS = [], PASTAS = [], filtro = '';
const nativo = !!(window.chrome && window.chrome.webview);
const mandar = (m) => nativo && window.chrome.webview.postMessage(JSON.stringify(m));

async function rest(caminho, opc = {}) {
  const r = await fetch(BANCO.url + '/rest/v1/' + caminho, {
    ...opc, headers: { apikey: BANCO.key, Authorization: 'Bearer ' + BANCO.key, 'Content-Type': 'application/json', ...(opc.headers || {}) }
  });
  if (!r.ok) throw new Error(r.status);
  const t = await r.text(); return t ? JSON.parse(t) : null;
}

async function carregar() {
  try {
    NOTAS = await rest('notas?select=id,titulo,corpo,cor,pasta,fixada,created_at&order=fixada.desc,created_at.desc&limit=200');
    try {
      const r = await rest('app_kv?k=eq.zimnotes_pastas&select=v');
      PASTAS = r.length ? JSON.parse(r[0].v) : [];
    } catch (e) { PASTAS = []; }
    for (const n of NOTAS) if (n.pasta && !PASTAS.includes(n.pasta)) PASTAS.push(n.pasta);
    render();
  } catch (e) { console.error('carregar', e); }
}

function categorias() {
  const set = new Set(PASTAS);
  for (const n of NOTAS) if (n.pasta) set.add(n.pasta);
  return [...set];
}

function renderChips() {
  const box = document.getElementById('chips');
  box.innerHTML = '';
  const chip = (rot, val, cor) => {
    const b = document.createElement('button');
    b.className = 'bchip' + (filtro === val ? ' on' : '');
    const q = val === '' ? NOTAS.length : NOTAS.filter(n => n.pasta === val).length;
    b.innerHTML = (cor ? `<i style="background:${cor}"></i>` : '') + esc(rot) + ` <span class="bn">${q}</span>`;
    b.onclick = () => { filtro = (filtro === val ? '' : val); render(); };
    box.appendChild(b);
  };
  chip('Todas', '', null);
  categorias().forEach(p => chip(p, p, corLista(p)));
}

function render() {
  renderChips();
  document.getElementById('conta').textContent =
    NOTAS.length + (NOTAS.length === 1 ? ' nota' : ' notas');
  const box = document.getElementById('lista');
  box.innerHTML = '';
  const lista = filtro === '' ? NOTAS : NOTAS.filter(n => n.pasta === filtro);
  if (!NOTAS.length) { box.innerHTML = '<div class="empty">nenhuma nota — usa o ＋ ali em cima</div>'; return; }
  if (!lista.length) { box.innerHTML = '<div class="empty">nada em "' + esc(filtro) + '"</div>'; return; }

  lista.forEach(n => {
    const el = document.createElement('div'); el.className = 'nt';
    const previa = semTags(n.corpo).split('\n').map(l => l.trim()).filter(Boolean)[0] || 'vazia';
    const cat = n.pasta ? `<span class="ncat">${esc(n.pasta)}</span>` : '';
    el.innerHTML = `<div class="fita" style="background:${corHex(n.cor)}"></div>
      <div class="ni"><div class="nt1">${(n.fixada ? '📌 ' : '') + esc(n.titulo || 'sem título')}</div>
        <div class="nt2">${esc(previa)}</div>${cat}</div>
      <button class="del" title="excluir nota">✕</button>`;
    el.querySelector('.del').onclick = (ev) => { ev.stopPropagation(); excluir(n); };
    el.onclick = () => mandar({ acao: 'abrirNota', id: n.id, titulo: n.titulo || '', corpo: n.corpo || '', cor: n.cor || '' });
    box.appendChild(el);
  });
}

async function excluir(n) {
  if (!confirm('Excluir "' + (n.titulo || 'sem título') + '"? Isso não volta.')) return;
  NOTAS = NOTAS.filter(x => x.id !== n.id); render();
  try { await rest('notas?id=eq.' + encodeURIComponent(n.id), { method: 'DELETE' }); }
  catch (e) { console.error('excluir', e); carregar(); }
}

/* arrastar as categorias pro lado (a barra de chips rola no arrasto) */
function arrastarChips(box) {
  let x0 = 0, s0 = 0, arrastando = false;
  box.addEventListener('pointerdown', (e) => {
    if (e.target.closest('.bchip')) return;   // toque num chip = clique, não arrasto
    arrastando = true; x0 = e.clientX; s0 = box.scrollLeft; box.setPointerCapture(e.pointerId);
  });
  box.addEventListener('pointermove', (e) => { if (arrastando) box.scrollLeft = s0 - (e.clientX - x0); });
  const fim = () => { arrastando = false; };
  box.addEventListener('pointerup', fim);
  box.addEventListener('pointercancel', fim);
  // e a roda do mouse rola de lado também
  box.addEventListener('wheel', (e) => { if (e.deltaY) { box.scrollLeft += e.deltaY; e.preventDefault(); } }, { passive: false });
}

/* ═══ JANELA (sem moldura) ═══ */
function ligarJanela() {
  const barra = document.getElementById('barra');
  barra.addEventListener('mousedown', e => {
    if (e.button !== 0 || e.target.closest('button')) return;
    mandar({ acao: 'arrastar' });
  });
  barra.addEventListener('dblclick', e => { if (!e.target.closest('button')) mandar({ acao: 'maximizar' }); });
  document.getElementById('btNova').onclick = () => mandar({ acao: 'novaNota' });
  document.getElementById('btFechar').onclick = () => mandar({ acao: 'fechar' });

  if (nativo) {
    const E = 6;
    const lados = [
      ['n', 'left:0;right:0;top:0;height:' + E + 'px', 'ns-resize'],
      ['s', 'left:0;right:0;bottom:0;height:' + E + 'px', 'ns-resize'],
      ['w', 'top:0;bottom:0;left:0;width:' + E + 'px', 'ew-resize'],
      ['e', 'top:0;bottom:0;right:0;width:' + E + 'px', 'ew-resize'],
      ['nw', 'left:0;top:0;width:14px;height:14px', 'nwse-resize'],
      ['ne', 'right:0;top:0;width:14px;height:14px', 'nesw-resize'],
      ['sw', 'left:0;bottom:0;width:14px;height:14px', 'nesw-resize'],
      ['se', 'right:0;bottom:0;width:14px;height:14px', 'nwse-resize']
    ];
    for (const [borda, pos, cursor] of lados) {
      const el = document.createElement('div');
      el.style.cssText = 'position:fixed;z-index:20;' + pos + ';cursor:' + cursor;
      el.addEventListener('mousedown', e => { if (e.button === 0) { e.preventDefault(); mandar({ acao: 'redimensionar', borda }); } });
      document.body.appendChild(el);
    }
  }

  // o C# avisa quando uma autoadesiva salvou/excluiu
  if (nativo) window.chrome.webview.addEventListener('message', ev => {
    const m = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data;
    if (m && m.evento === 'recarregar') carregar();
  });
}

document.documentElement.dataset.theme =
  localStorage.getItem('zimbar-tema') === 'escuro' ? 'dark' : '';
ligarJanela();
arrastarChips(document.getElementById('chips'));
carregar();
setInterval(() => { if (document.visibilityState === 'visible') carregar(); }, 15000);
