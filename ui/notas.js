/* ═══════════════════════════════════════════════════════════════
   ZIMNOTES — a mesma tabela `notas` do celular, na tela do computador.

   Um arquivo só, usado por dois lugares: a aba Notas dentro do Zimbar e
   a janela solta do ZimNotes (notas.html). É por isso que os dois são
   idênticos — não tem duas versões pra manter em dia.

   notas: id · titulo · corpo · rodape · cor · data_nota · created_at
          fixada · pasta · tags · itens · fotos
   app_kv: zimnotes_pastas → o rol de pastas, pra pasta vazia não sumir
   ═══════════════════════════════════════════════════════════════ */
const ZimNotes = (() => {

const BANCO = {
  url: 'https://fautswjwioiviqvpgsrw.supabase.co',
  key: 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImZhdXRzd2p3aW9pdmlxdnBnc3J3Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzk4ODkxNzgsImV4cCI6MjA5NTQ2NTE3OH0.99cWW-JA8-fH_wj_tqNLdpgubhM98UleH-0D5YaENM4'
};

/* as seis cores são as mesmas do celular; a chave vazia é o amarelo */
const CORES = [
  { k: '',      nome: 'Amarelo', hex: '#FFF3C4' },
  { k: 'uva',   nome: 'Uva',     hex: '#E6DAFF' },
  { k: 'vinho', nome: 'Vinho',   hex: '#FFD5E9' },
  { k: 'mel',   nome: 'Mel',     hex: '#FFE4B5' },
  { k: 'mata',  nome: 'Mata',    hex: '#D2F2DF' },
  { k: 'noite', nome: 'Noite',   hex: '#D9E9FF' }
];
const daCor = (k) => CORES.find(c => c.k === (k || '')) || CORES[0];

function corDaPasta(nome) {
  let h = 0;
  for (const ch of String(nome)) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return CORES[h % CORES.length];
}

/* ═══ ESTADO ═══ */
let NOTAS = [];
let PASTAS = [];
let filtro = { tipo: 'tudo', valor: null };
let abertaId = null;
let busca = '';
let relogioSalvar = null;
let raiz = null;              // o elemento onde o ZimNotes foi montado
let relogioSync = null;
let ondeEstava = null;        // cursor guardado, pros botões da barra
const SUBINDO = [];           // fotos em trânsito

const q  = (s) => raiz.querySelector(s);
const qq = (s) => [...raiz.querySelectorAll(s)];
const esc = (s) => String(s ?? '').replace(/[<>&]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' }[c]));

/* ═══ REDE ═══ */
async function chamar(caminho, opc = {}) {
  const r = await fetch(BANCO.url + '/rest/v1/' + caminho, {
    ...opc,
    headers: {
      apikey: BANCO.key,
      Authorization: 'Bearer ' + BANCO.key,
      'Content-Type': 'application/json',
      ...(opc.headers || {})
    }
  });
  if (!r.ok) throw new Error(r.status + ' — ' + (await r.text()).slice(0, 140));
  const t = await r.text();
  return t ? JSON.parse(t) : null;
}

function recado(txt, erro) {
  const el = q('#znSinal');
  if (!el) return;
  el.textContent = txt || '';
  el.style.color = erro ? 'var(--zacc)' : 'var(--zfaint)';
  if (txt && !erro) setTimeout(() => { if (el.textContent === txt) el.textContent = ''; }, 1400);
}

/* ═══ TEXTO ═══
   O corpo virou campo rico pra existir negrito e sublinhado. Quem já era
   texto puro continua sendo: só vira HTML quando você formata alguma
   coisa. Prévia, busca e cópia passam por textoPuro. */
const PARECE_HTML = /<(b|i|u|br|div|p|strong|em|span)\b[^>]*>/i;

function limpar(html) {
  const caixa = document.createElement('div');
  caixa.innerHTML = html;
  caixa.querySelectorAll('script,style,iframe,object,embed,link').forEach(el => el.remove());
  caixa.querySelectorAll('*').forEach(el => {
    for (const a of [...el.attributes])
      if (/^on/i.test(a.name) || (a.name === 'href' && /^\s*javascript:/i.test(a.value)))
        el.removeAttribute(a.name);
  });
  return caixa.innerHTML;
}
function paraEditor(txt) {
  const t = String(txt || '');
  return PARECE_HTML.test(t) ? limpar(t) : esc(t).replace(/\n/g, '<br>');
}
function textoPuro(txt) {
  const t = String(txt || '');
  if (!PARECE_HTML.test(t)) return t;
  const caixa = document.createElement('div');
  caixa.innerHTML = t.replace(/<\/(div|p)>/gi, '\n').replace(/<br\s*\/?>/gi, '\n');
  return (caixa.textContent || '').replace(/\n{3,}/g, '\n\n');
}
function doEditor(el) {
  const html = limpar(el.innerHTML).replace(/^(<br\s*\/?>)+|(<br\s*\/?>)+$/gi, '');
  return /<(b|i|u|strong|em|span)\b/i.test(html) ? html : textoPuro(html);
}

const palavras = (t) => (String(t || '').trim().match(/\S+/g) || []).length;

function quando(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  const dias = Math.floor((new Date().setHours(0, 0, 0, 0) - new Date(iso).setHours(0, 0, 0, 0)) / 864e5);
  if (dias <= 0) return 'hoje';
  if (dias === 1) return 'ontem';
  if (dias < 7) return `há ${dias} dias`;
  if (dias < 14) return 'semana passada';
  if (dias < 60) return `há ${Math.floor(dias / 7)} semanas`;
  return d.toLocaleDateString('pt-BR', { day: 'numeric', month: 'short' });
}

const dataNota = () =>
  new Date().toLocaleDateString('pt-BR', { day: 'numeric', month: 'short', year: 'numeric' });

function novoId() {
  const abc = 'abcdefghijklmnopqrstuvwxyz0123456789';
  let s = '';
  for (let i = 0; i < 12; i++) s += abc[Math.floor(Math.random() * abc.length)];
  return s;
}

function arrumar(n) {
  n.tags  = Array.isArray(n.tags)  ? n.tags  : [];
  n.itens = Array.isArray(n.itens) ? n.itens : [];
  n.fotos = Array.isArray(n.fotos) ? n.fotos : [];
  n.pasta = n.pasta || '';
  n.rodape = n.rodape || '';
  n.fixada = !!n.fixada;
  return n;
}

const notaAberta = () => NOTAS.find(x => x.id === abertaId);
const primeiraLinha = (n) => {
  const t = (n.titulo || '').trim();
  if (t) return t;
  return textoPuro(n.corpo).trim().split('\n')[0] || 'Sem título';
};
const resumo = (n) => {
  const corpo = textoPuro(n.corpo).trim();
  const semTitulo = !(n.titulo || '').trim();
  const t = (semTitulo ? corpo.split('\n').slice(1).join(' ') : corpo) + ' ' + textoPuro(n.rodape);
  return t.replace(/\s+/g, ' ').trim();
};
const estaEmBranco = (n) =>
  !(n.titulo || '').trim() && !textoPuro(n.corpo).trim() &&
  !textoPuro(n.rodape).trim() && !n.itens.length && !n.fotos.length;

const ordenadas = (lista) => [...lista].sort((a, b) => (b.fixada ? 1 : 0) - (a.fixada ? 1 : 0));

/* ═══ CARREGAR ═══ */
async function carregarPastas() {
  try {
    const r = await chamar('app_kv?k=eq.zimnotes_pastas&select=v');
    PASTAS = r.length ? JSON.parse(r[0].v) : [];
  } catch (e) { PASTAS = []; }
  for (const n of NOTAS) if (n.pasta && !PASTAS.includes(n.pasta)) PASTAS.push(n.pasta);
}

async function gravarPastas() {
  try {
    await chamar('app_kv', {
      method: 'POST',
      headers: { Prefer: 'resolution=merge-duplicates' },
      body: JSON.stringify({ k: 'zimnotes_pastas', v: JSON.stringify(PASTAS), updated_at: new Date().toISOString() })
    });
  } catch (e) { console.error('pastas', e); }
}

async function carregar(silencioso) {
  if (!silencioso) recado('sincronizando');
  try {
    const doServidor = (await chamar('notas?select=*&order=created_at.desc&limit=300')).map(arrumar);

    // A nota aberta e as que ainda não chegaram ao banco continuam sendo os
    // objetos daqui: uma sincronia no meio da digitação não pode trocar o
    // chão embaixo do que você está escrevendo.
    const meus = new Map(NOTAS.filter(n => n._nova || n.id === abertaId).map(n => [n.id, n]));
    const juntas = doServidor.map(s => meus.get(s.id) || s);
    for (const [id, n] of meus) if (!doServidor.some(s => s.id === id)) juntas.unshift(n);
    NOTAS = juntas;

    await carregarPastas();
    pintarLado();
    if (!silencioso) recado('');
  } catch (e) {
    console.error('notas: carregar', e);
    recado('sem conexão', true);
  }
}

/* ═══ ESQUELETO ═══ */
const SVG = {
  lupa:  '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/></svg>',
  mais:  '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>',
  pin:   '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"><path d="M12 17v5M9 3h6l-1 6 3 3v2H7v-2l3-3-1-6z"/></svg>',
  lista: '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6l2 2 3-3M4 13l2 2 3-3M4 20l2 2 3-3M13 7h7M13 14h7M13 21h7"/></svg>',
  foto:  '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="6" width="18" height="14" rx="3"/><circle cx="12" cy="13" r="3.4"/><path d="M8 6l1.5-2h5L16 6"/></svg>',
  pasta: '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a2 2 0 012-2h4l2 2.5h8a2 2 0 012 2V18a2 2 0 01-2 2H5a2 2 0 01-2-2z"/></svg>',
  etq:   '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12V5a2 2 0 012-2h7l9 9-9 9z"/><circle cx="7.5" cy="7.5" r="1.4"/></svg>',
  cor:   '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><circle cx="9" cy="9.5" r="1.3" fill="currentColor" stroke="none"/><circle cx="15" cy="9.5" r="1.3" fill="currentColor" stroke="none"/><circle cx="10" cy="15" r="1.3" fill="currentColor" stroke="none"/></svg>',
  lixo:  '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 7h16M9 7V5h6v2M6 7l1 13h10l1-13"/></svg>',
  x:     '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="M6 6l12 12M18 6L6 18"/></svg>',
  ok:    '<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3.4" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7"/></svg>',
  grip:  '<svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><circle cx="9" cy="6" r="1.7"/><circle cx="15" cy="6" r="1.7"/><circle cx="9" cy="12" r="1.7"/><circle cx="15" cy="12" r="1.7"/><circle cx="9" cy="18" r="1.7"/><circle cx="15" cy="18" r="1.7"/></svg>'
};

const ESQUELETO = `
<div class="zn-lado">
  <div class="zn-topo">
    <div class="zn-busca">${SVG.lupa}<input id="znBusca" placeholder="buscar nas notas" autocomplete="off"></div>
    <button class="zn-mais" id="znNova" title="nova nota (Ctrl+N)">${SVG.mais}</button>
  </div>
  <div class="zn-chips" id="znChips"></div>
  <div class="zn-lista" id="znLista"></div>
</div>

<div class="zn-editor" id="znEditor">
  <div class="zn-cabeca">
    <span id="znSinal" style="font-size:10.5px;font-weight:700;letter-spacing:.08em;
          text-transform:uppercase;color:var(--zfaint)"></span>
    <span class="sp"></span>
    <button class="zn-redondo" id="znFixar" title="fixar no topo">${SVG.pin}</button>
    <button class="zn-redondo" id="znApagar" title="apagar nota">${SVG.lixo}</button>
  </div>

  <div class="zn-corpo" id="znCorpo">
    <textarea class="zn-titulo" id="znTitulo" rows="1" placeholder="Sem título"></textarea>
    <div class="zn-meta"><span id="znQuando"></span><span id="znPalavras"></span></div>
    <div class="zn-texto" id="znTexto" contenteditable="true" data-vazio="Escreve aqui…"></div>
    <div id="znFotos"></div>
    <div id="znItens"></div>
    <div class="zn-texto zn-rodape" id="znRodape" contenteditable="true"
         data-vazio="…e aqui embaixo da lista"></div>
  </div>

  <div class="zn-pe">
    <div class="zn-marcas" id="znMarcas"></div>
    <div class="zn-paleta" id="znPaleta"></div>
    <div class="zn-ferramentas">
      <button id="znNegrito" title="negrito (Ctrl+B)" style="font-weight:800;font-size:15px">B</button>
      <button id="znSublinhado" title="sublinhado (Ctrl+U)"
              style="font-size:15px;text-decoration:underline;text-underline-offset:3px">S</button>
      <span class="risco"></span>
      <button id="znLista" title="item de lista">${SVG.lista}</button>
      <button id="znFoto" title="foto">${SVG.foto}</button>
      <button id="znPasta" title="coleção">${SVG.pasta}</button>
      <span class="sp"></span>
      <button id="znEtiqueta" title="etiqueta">${SVG.etq}</button>
      <button id="znCor" title="cor da nota">${SVG.cor}</button>
    </div>
  </div>
  <input type="file" id="znArquivo" accept="image/*" hidden>
</div>

<div class="zn-editor" id="znNada" style="--cn:transparent">
  <div class="zn-nada"><b>Nenhuma nota aberta</b>
    Escolhe uma da lista à esquerda, ou clica no + pra escrever uma nova.</div>
</div>`;

/* o editor e o "nada aberto" dividem a mesma coluna; um de cada vez */
function mostrarEditor(sim) {
  q('#znEditor').style.display = sim ? 'flex' : 'none';
  q('#znNada').style.display = sim ? 'none' : 'flex';
}

/* ═══ LISTA ═══ */
function notasFiltradas() {
  let l = NOTAS;
  if (filtro.tipo === 'pasta')    l = l.filter(n => n.pasta === filtro.valor);
  if (filtro.tipo === 'soltas')   l = l.filter(n => !n.pasta);
  if (filtro.tipo === 'etiqueta') l = l.filter(n => n.tags.includes(filtro.valor));
  const t = busca.trim().toLowerCase();
  if (t) l = l.filter(n =>
    (n.titulo || '').toLowerCase().includes(t) ||
    textoPuro(n.corpo).toLowerCase().includes(t) ||
    textoPuro(n.rodape).toLowerCase().includes(t) ||
    (n.pasta || '').toLowerCase().includes(t) ||
    n.tags.some(x => x.toLowerCase().includes(t)) ||
    n.itens.some(i => (i.t || '').toLowerCase().includes(t)));
  return l;
}

function pintarChips() {
  const box = q('#znChips');
  box.innerHTML = '';
  const chip = (rot, ligado, aoTocar, cor, quantos) => {
    const b = document.createElement('button');
    b.className = 'zn-chip' + (ligado ? ' on' : '');
    b.innerHTML = (cor ? `<i style="background:${cor}"></i>` : '') + esc(rot) +
                  (quantos != null ? ` <span class="n">${quantos}</span>` : '');
    b.onclick = aoTocar;
    box.appendChild(b);
  };
  const trocar = (novo) => { filtro = novo; pintarLado(); };

  chip('Todas', filtro.tipo === 'tudo', () => trocar({ tipo: 'tudo', valor: null }), null, NOTAS.length);
  PASTAS.forEach(p => {
    const n = NOTAS.filter(x => x.pasta === p).length;
    chip(p, filtro.tipo === 'pasta' && filtro.valor === p,
      () => trocar(filtro.tipo === 'pasta' && filtro.valor === p
        ? { tipo: 'tudo', valor: null } : { tipo: 'pasta', valor: p }),
      corDaPasta(p).hex, n);
  });
  if (PASTAS.length && NOTAS.some(n => !n.pasta))
    chip('Soltas', filtro.tipo === 'soltas',
      () => trocar(filtro.tipo === 'soltas' ? { tipo: 'tudo', valor: null } : { tipo: 'soltas', valor: null }),
      null, NOTAS.filter(n => !n.pasta).length);
  if (filtro.tipo === 'etiqueta')
    chip('#' + filtro.valor, true, () => trocar({ tipo: 'tudo', valor: null }),
      corDaPasta(filtro.valor).hex, notasFiltradas().length);
}

function pintarLista() {
  const box = q('#znLista');
  const lista = ordenadas(notasFiltradas());
  box.innerHTML = '';

  if (!lista.length) {
    box.innerHTML = NOTAS.length
      ? `<div class="zn-vazio"><b>Nada aqui</b>Tenta outro filtro ou outra palavra.</div>`
      : `<div class="zn-vazio"><b>Ainda não tem nota nenhuma</b>Toca no + pra escrever a primeira.</div>`;
    return;
  }

  lista.forEach(n => {
    const el = document.createElement('button');
    el.className = 'zn-nota' + (n.id === abertaId ? ' on' : '');
    el.style.setProperty('--cn', daCor(n.cor).hex);
    const txt = resumo(n);
    const abertos = n.itens.filter(i => !i.f);
    const mostra = (abertos.length ? abertos : n.itens).slice(0, 2);
    el.innerHTML =
      (n.fixada ? `<span class="alfinete">${SVG.pin}</span>` : '') +
      `<h3>${esc(primeiraLinha(n))}</h3>` +
      (n.fotos.length ? `<img class="capa" src="${esc(n.fotos[0].url)}" alt="" loading="lazy">` : '') +
      (txt ? `<p>${esc(txt)}</p>` : '') +
      (mostra.length
        ? `<div class="previa">` + mostra.map(i =>
            `<div><span class="cx${i.f ? ' f' : ''}"></span><span class="${i.f ? 'feito' : ''}">${esc(i.t)}</span></div>`
          ).join('') +
          (n.itens.length > 2 ? `<div style="opacity:.55">+${n.itens.length - 2}</div>` : '') + `</div>`
        : '') +
      (n.tags.length
        ? `<div class="etq">` + n.tags.slice(0, 3).map(t => `<span>${esc(t)}</span>`).join('') + `</div>`
        : '') +
      `<div class="quando">${quando(n.created_at)}</div>`;
    el.onclick = () => abrirNota(n.id);
    box.appendChild(el);
  });
}

function pintarLado() {
  pintarChips();
  pintarLista();
  const ct = document.getElementById('ct-notas');
  if (ct) ct.textContent = NOTAS.length || '';
}

/* ═══ EDITOR ═══ */
function crescer(el) { el.style.height = 'auto'; el.style.height = el.scrollHeight + 'px'; }

function abrirNota(id) {
  const n = NOTAS.find(x => x.id === id);
  if (!n) return;
  if (abertaId && abertaId !== id) { clearTimeout(relogioSalvar); gravar(); }
  abertaId = id;
  mostrarEditor(true);
  q('#znTitulo').value = n.titulo || '';
  q('#znTexto').innerHTML = paraEditor(n.corpo);
  q('#znRodape').innerHTML = paraEditor(n.rodape);
  q('#znPaleta').classList.remove('on');
  pintarPaleta(n.cor || '');
  pintarEditor();
  pintarLista();
  requestAnimationFrame(() => crescer(q('#znTitulo')));
}

/* Nasce na tela na hora e vai pro banco depois: esperar o Supabase
   responder pra só então mostrar a nota é o que dava aquela demora. */
function novaNota() {
  const nova = arrumar({
    id: novoId(), titulo: '', corpo: '', rodape: '', cor: '',
    data_nota: dataNota(), created_at: new Date().toISOString(),
    pasta: filtro.tipo === 'pasta' ? filtro.valor : '',
    tags: [], itens: [], fotos: [], fixada: false,
    _nova: true
  });
  NOTAS.unshift(nova);
  abrirNota(nova.id);
  pintarLado();
  setTimeout(() => q('#znTitulo').focus(), 30);
}

function pintarMeta() {
  const n = notaAberta();
  if (!n) return;
  const p = palavras(textoPuro(n.corpo) + ' ' + textoPuro(n.rodape));
  q('#znQuando').textContent = n._nova ? 'agora' : quando(n.created_at);
  q('#znPalavras').textContent = p ? '· ' + p + (p === 1 ? ' palavra' : ' palavras') : '';
  q('#znEditor').style.setProperty('--cn', daCor(n.cor).hex);
  q('#znFixar').classList.toggle('ativa', n.fixada);
  const temMeio = n.itens.length || n.fotos.length;
  q('#znRodape').style.display = (temMeio || textoPuro(n.rodape).trim()) ? '' : 'none';
}

function pintarEditor() {
  const n = notaAberta();
  if (!n) return;
  pintarMeta();
  pintarFotos(n);
  pintarItens(n);
  pintarMarcas(n);
}

/* ── fotos ── */
function pintarFotos(n) {
  const box = q('#znFotos');
  const subindo = SUBINDO.filter(f => f.nota === n.id);
  if (!n.fotos.length && !subindo.length) { box.innerHTML = ''; return; }
  box.innerHTML = '<div class="zn-fotos"></div>';
  const grade = box.firstElementChild;

  n.fotos.forEach((f, i) => {
    const el = document.createElement('div');
    el.className = 'zn-foto';
    el.innerHTML = `<img src="${esc(f.url)}" alt="" loading="lazy">
                    <button class="tira" title="tirar foto">${SVG.x}</button>`;
    el.querySelector('img').onclick = () => abrirFora(f.url);
    el.querySelector('.tira').onclick = () => removerFoto(i);
    grade.appendChild(el);
  });
  subindo.forEach(() => {
    const el = document.createElement('div');
    el.className = 'zn-foto subindo';
    el.textContent = 'subindo…';
    grade.appendChild(el);
  });
}

function abrirFora(url) {
  if (typeof Ponte !== 'undefined' && Ponte.tem()) Ponte.enviar({ acao: 'abrirLink', url });
  else window.open(url, '_blank');
}

function encolher(arquivo, lado = 1600) {
  return new Promise((ok, erro) => {
    const img = new Image();
    img.onload = () => {
      const escala = Math.min(1, lado / Math.max(img.width, img.height));
      if (escala === 1 && arquivo.size < 900e3) { ok(arquivo); return; }
      const tela = document.createElement('canvas');
      tela.width = Math.round(img.width * escala);
      tela.height = Math.round(img.height * escala);
      tela.getContext('2d').drawImage(img, 0, 0, tela.width, tela.height);
      tela.toBlob(b => ok(b || arquivo), 'image/jpeg', 0.85);
      URL.revokeObjectURL(img.src);
    };
    img.onerror = () => erro(new Error('imagem ilegível'));
    img.src = URL.createObjectURL(arquivo);
  });
}

async function subirFoto(notaId, arquivo) {
  const menor = await encolher(arquivo);
  const ext = (menor.type || '').includes('png') ? 'png' : 'jpg';
  const caminho = `${notaId}/${novoId()}.${ext}`;
  const r = await fetch(`${BANCO.url}/storage/v1/object/notas/${caminho}`, {
    method: 'POST',
    headers: {
      apikey: BANCO.key, Authorization: 'Bearer ' + BANCO.key,
      'Content-Type': menor.type || 'image/jpeg', 'x-upsert': 'true'
    },
    body: menor
  });
  if (!r.ok) throw new Error(r.status + ' — ' + (await r.text()).slice(0, 140));
  return `${BANCO.url}/storage/v1/object/public/notas/${caminho}`;
}

async function porFoto(arquivo) {
  const n = notaAberta();
  if (!arquivo || !n) return;
  const marca = { nota: n.id };
  SUBINDO.push(marca);
  pintarFotos(n);
  try {
    const url = await subirFoto(n.id, arquivo);
    n.fotos.push({ id: novoId(), url });
    agendarGravar();
  } catch (e) {
    console.error('foto', e);
    recado('não subiu a foto', true);
  } finally {
    SUBINDO.splice(SUBINDO.indexOf(marca), 1);
    pintarEditor();
  }
}

async function removerFoto(i) {
  const n = notaAberta();
  if (!n || !n.fotos[i]) return;
  const [f] = n.fotos.splice(i, 1);
  pintarEditor();
  agendarGravar();
  const caminho = String(f.url).split('/public/notas/')[1];
  if (!caminho) return;
  try {
    await fetch(`${BANCO.url}/storage/v1/object/notas/${caminho}`, {
      method: 'DELETE', headers: { apikey: BANCO.key, Authorization: 'Bearer ' + BANCO.key }
    });
  } catch (e) { console.warn('apagar foto', e); }
}

/* ── checklist ── */
function pintarItens(n) {
  const box = q('#znItens');
  if (!n.itens.length) { box.innerHTML = ''; return; }
  box.innerHTML = '<div class="zn-itens" id="znCaixaItens"></div>';
  const caixa = q('#znCaixaItens');

  n.itens.forEach((it, i) => {
    const linha = document.createElement('div');
    linha.className = 'zn-item' + (it.f ? ' feito' : '');
    linha.innerHTML =
      `<button class="zn-pega" title="arrastar">${SVG.grip}</button>
       <button class="cx${it.f ? ' f' : ''}" title="marcar">${SVG.ok}</button>
       <input value="${esc(it.t)}" placeholder="item">
       <button class="lixo" title="remover">${SVG.x}</button>`;
    arrastarItem(linha.querySelector('.zn-pega'), linha, i);
    linha.querySelector('.cx').onclick = () => { it.f = !it.f; pintarItens(n); agendarGravar(); };
    const campo = linha.querySelector('input');
    campo.oninput = () => { it.t = campo.value; agendarGravar(); };
    campo.onkeydown = (e) => {
      if (e.key === 'Enter') { e.preventDefault(); novoItem(i + 1); }
      if (e.key === 'Backspace' && campo.value === '' && n.itens.length > 1) {
        e.preventDefault(); removerItem(i, true);
      }
    };
    linha.querySelector('.lixo').onclick = () => removerItem(i);
    caixa.appendChild(linha);
  });

  const add = document.createElement('button');
  add.className = 'zn-add';
  add.innerHTML = SVG.mais + ' novo item';
  add.onclick = () => novoItem();
  caixa.appendChild(add);
}

/* Arrastar pela alça reordena. Ponteiro cru, não o drag-and-drop do HTML5:
   o mesmo código serve pro dedo no celular e pro mouse aqui. */
function arrastarItem(alca, linha, indice) {
  alca.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    const n = notaAberta();
    if (!n) return;
    const caixa = linha.parentElement;
    const linhas = [...caixa.querySelectorAll('.zn-item')];
    const caixas = linhas.map(l => l.getBoundingClientRect());
    const y0 = e.clientY;
    let destino = indice;

    linha.classList.add('arrastando');
    try { alca.setPointerCapture(e.pointerId); } catch (err) { /* mouse sem captura */ }

    const mover = (ev) => {
      const dy = ev.clientY - y0;
      linha.style.transform = `translateY(${dy}px)`;
      const centro = caixas[indice].top + caixas[indice].height / 2 + dy;
      destino = caixas.findIndex((r, k) => centro < r.top + r.height / 2 || k === caixas.length - 1);
      if (destino < 0) destino = caixas.length - 1;
      linhas.forEach((l, k) => {
        if (k === indice) return;
        const desloca = (k > indice && k <= destino) ? -caixas[indice].height
                      : (k < indice && k >= destino) ? caixas[indice].height : 0;
        l.style.transform = `translateY(${desloca}px)`;
      });
    };
    const soltar = () => {
      alca.removeEventListener('pointermove', mover);
      alca.removeEventListener('pointerup', soltar);
      alca.removeEventListener('pointercancel', soltar);
      linhas.forEach(l => { l.style.transform = ''; l.classList.remove('arrastando'); });
      if (destino !== indice && destino >= 0) {
        const [m] = n.itens.splice(indice, 1);
        n.itens.splice(destino, 0, m);
        agendarGravar();
      }
      pintarEditor();
    };
    alca.addEventListener('pointermove', mover);
    alca.addEventListener('pointerup', soltar);
    alca.addEventListener('pointercancel', soltar);
  });
}

function novoItem(posicao) {
  const n = notaAberta();
  if (!n) return;
  const i = posicao == null ? n.itens.length : posicao;
  n.itens.splice(i, 0, { id: novoId(), t: '', f: false });
  pintarEditor();
  // depois do quadro: redesenhar troca os elementos, e focar o antigo não
  // leva o cursor a lugar nenhum
  requestAnimationFrame(() => {
    const campo = qq('#znCaixaItens .zn-item input')[i];
    if (campo) { campo.focus(); campo.setSelectionRange(campo.value.length, campo.value.length); }
  });
  agendarGravar();
}

function removerItem(i, focarAnterior) {
  const n = notaAberta();
  if (!n) return;
  n.itens.splice(i, 1);
  pintarEditor();
  if (focarAnterior) requestAnimationFrame(() => {
    const alvo = qq('#znCaixaItens .zn-item input')[Math.max(0, i - 1)];
    if (alvo) { alvo.focus(); alvo.setSelectionRange(alvo.value.length, alvo.value.length); }
  });
  agendarGravar();
}

/* ── coleção e etiquetas ── */
function pintarMarcas(n) {
  const box = q('#znMarcas');
  box.innerHTML = '';
  if (n.pasta) {
    const b = document.createElement('button');
    b.className = 'zn-marca';
    b.innerHTML = `<i style="width:7px;height:7px;border-radius:50%;background:${corDaPasta(n.pasta).hex}"></i>` +
                  esc(n.pasta) + `<span class="x">✕</span>`;
    b.onclick = (e) => {
      if (e.target.classList.contains('x')) { n.pasta = ''; pintarEditor(); pintarLado(); agendarGravar(); }
      else escolherPasta();
    };
    box.appendChild(b);
  }
  n.tags.forEach((t, i) => {
    const b = document.createElement('button');
    b.className = 'zn-marca';
    b.innerHTML = '#' + esc(t) + `<span class="x">✕</span>`;
    b.onclick = () => { n.tags.splice(i, 1); pintarEditor(); pintarLado(); agendarGravar(); };
    box.appendChild(b);
  });
}

function abrirGaveta(html) {
  fecharGaveta();
  const veu = document.createElement('div');
  veu.className = 'zn-veu';
  veu.id = 'znVeu';
  veu.innerHTML = `<div class="zn-gaveta">${html}</div>`;
  veu.onclick = (e) => { if (e.target === veu) fecharGaveta(); };
  // dentro da raiz do ZimNotes, pra herdar as variáveis de cor e o tema
  raiz.appendChild(veu);
  return veu;
}
function fecharGaveta() {
  const v = raiz && raiz.querySelector('#znVeu');
  if (v) v.remove();
}

function escolherPasta() {
  const n = notaAberta();
  if (!n) return;
  const linhas = PASTAS.map(p => {
    const c = corDaPasta(p);
    return `<button class="zn-opcao" data-p="${esc(p)}">
      <span class="tile" style="background:${c.hex}">${SVG.pasta}</span>${esc(p)}
      ${n.pasta === p ? '<span class="ok">✓</span>' : ''}</button>`;
  }).join('');

  abrirGaveta(
    `<h2>Coleção</h2><div class="dica">Onde essa nota mora.</div>` +
    (n.pasta ? `<button class="zn-opcao" data-p=""><span class="tile" style="background:var(--zline-2)"></span>Sem coleção</button>` : '') +
    linhas +
    `<input class="campo" id="znPastaNova" placeholder="Nova coleção…" style="margin-top:13px">
     <button class="zn-botao" id="znCriarPasta">Criar e usar</button>`);

  qq('#znVeu .zn-opcao').forEach(b => b.onclick = () => {
    n.pasta = b.dataset.p;
    fecharGaveta(); pintarEditor(); pintarLado(); agendarGravar();
  });
  const criar = async () => {
    const nome = q('#znPastaNova').value.trim();
    if (!nome) return;
    if (!PASTAS.includes(nome)) { PASTAS.push(nome); await gravarPastas(); }
    n.pasta = nome;
    fecharGaveta(); pintarEditor(); pintarLado(); agendarGravar();
  };
  q('#znCriarPasta').onclick = criar;
  q('#znPastaNova').onkeydown = (e) => { if (e.key === 'Enter') criar(); };
  setTimeout(() => q('#znPastaNova').focus(), 40);
}

function novaEtiqueta() {
  const n = notaAberta();
  if (!n) return;
  const usadas = [...new Set(NOTAS.flatMap(x => x.tags))].filter(t => !n.tags.includes(t));
  abrirGaveta(
    `<h2>Etiqueta</h2><div class="dica">Marca livre, sem pasta. Uma nota pode ter várias.</div>
     <input class="campo" id="znEtqNova" placeholder="escrita, o gato, meia-boca…">
     <button class="zn-botao" id="znCriarEtq">Adicionar</button>` +
    (usadas.length
      ? `<div style="display:flex;flex-wrap:wrap;gap:6px;margin-top:14px">` +
        usadas.map(t => `<button class="zn-chip" data-t="${esc(t)}">#${esc(t)}</button>`).join('') + `</div>`
      : ''));

  const por = (t) => {
    t = String(t).trim().replace(/^#/, '');
    if (!t || n.tags.includes(t)) { fecharGaveta(); return; }
    n.tags.push(t);
    fecharGaveta(); pintarEditor(); pintarLado(); agendarGravar();
  };
  q('#znCriarEtq').onclick = () => por(q('#znEtqNova').value);
  q('#znEtqNova').onkeydown = (e) => { if (e.key === 'Enter') por(e.target.value); };
  qq('#znVeu .zn-chip').forEach(b => b.onclick = () => por(b.dataset.t));
  setTimeout(() => q('#znEtqNova').focus(), 40);
}

/* ── cor ── */
function pintarPaleta(atual) {
  const box = q('#znPaleta');
  box.innerHTML = '';
  CORES.forEach(c => {
    const b = document.createElement('button');
    b.className = 'zn-tinta' + (c.k === (atual || '') ? ' on' : '');
    b.style.background = c.hex;
    b.title = c.nome;
    b.onclick = () => {
      const n = notaAberta();
      if (!n) return;
      n.cor = c.k;
      pintarPaleta(c.k);
      pintarMeta();
      pintarLista();
      agendarGravar();
      box.classList.remove('on');
    };
    box.appendChild(b);
  });
}

function alternarFixada() {
  const n = notaAberta();
  if (!n) return;
  n.fixada = !n.fixada;
  pintarMeta();
  pintarLado();
  agendarGravar();
}

/* ═══ ESCREVER ═══ */
function aoDigitar() {
  const n = notaAberta();
  if (!n) return;
  n.titulo = q('#znTitulo').value;
  n.corpo = doEditor(q('#znTexto'));
  n.rodape = doEditor(q('#znRodape'));
  crescer(q('#znTitulo'));
  pintarMeta();
  agendarGravar();
}

function lembrarCursor() {
  const s = window.getSelection();
  if (!s.rangeCount) return;
  const dentro = qq('.zn-texto').some(el => el.contains(s.anchorNode));
  if (dentro) ondeEstava = { campo: document.activeElement, faixa: s.getRangeAt(0).cloneRange() };
}

function devolverCursor() {
  if (!ondeEstava || !ondeEstava.campo || !ondeEstava.campo.isConnected) return false;
  ondeEstava.campo.focus();
  const s = window.getSelection();
  s.removeAllRanges();
  s.addRange(ondeEstava.faixa);
  return true;
}

function formatar(cmd) {
  if (!devolverCursor()) q('#znTexto').focus();
  document.execCommand(cmd, false, null);
  lembrarCursor();
  aoDigitar();
  pintarBotoesDeFormato();
}

function pintarBotoesDeFormato() {
  for (const [id, cmd] of [['#znNegrito', 'bold'], ['#znSublinhado', 'underline']]) {
    let liga = false;
    try { liga = document.queryCommandState(cmd); } catch (e) { /* fora de campo rico */ }
    const b = q(id);
    if (b) b.classList.toggle('on', liga);
  }
}

/* ═══ GRAVAR ═══ */
function agendarGravar() {
  clearTimeout(relogioSalvar);
  relogioSalvar = setTimeout(gravar, 700);
}

async function gravar() {
  const n = notaAberta();
  if (!n) return;
  if (estaEmBranco(n)) return;

  recado('salvando');
  const linha = {
    id: n.id, titulo: n.titulo || '', corpo: n.corpo || '', rodape: n.rodape || '',
    cor: n.cor || '', data_nota: n.data_nota || dataNota(),
    fixada: !!n.fixada, pasta: n.pasta || '', tags: n.tags,
    itens: n.itens.filter(i => (i.t || '').trim()), fotos: n.fotos
  };
  try {
    if (n._nova) {
      linha.created_at = n.created_at;
      await chamar('notas', { method: 'POST', body: JSON.stringify(linha) });
      delete n._nova;
    } else {
      await chamar('notas?id=eq.' + encodeURIComponent(n.id),
                   { method: 'PATCH', body: JSON.stringify(linha) });
    }
    recado('');
    pintarLista();
  } catch (e) {
    console.error('notas: gravar', e);
    recado('não salvou', true);
  }
}

async function apagarNota() {
  const n = notaAberta();
  if (!n) return;
  if (!confirm('Apagar "' + primeiraLinha(n) + '"?')) return;
  const id = n.id, eraNova = n._nova;
  clearTimeout(relogioSalvar);
  NOTAS = NOTAS.filter(x => x.id !== id);
  abertaId = null;
  mostrarEditor(false);
  pintarLado();
  if (eraNova) return;
  try { await chamar('notas?id=eq.' + encodeURIComponent(id), { method: 'DELETE' }); }
  catch (e) { console.error('notas: apagar', e); recado('não apagou', true); }
}

/* ═══ MONTAGEM ═══ */
function montar(el) {
  if (raiz === el && el.dataset.pronto) { carregar(true); return; }
  raiz = el;
  raiz.classList.add('zn');
  raiz.innerHTML = ESQUELETO;
  raiz.dataset.pronto = '1';
  mostrarEditor(!!abertaId);

  q('#znNova').onclick = novaNota;
  q('#znFixar').onclick = alternarFixada;
  q('#znApagar').onclick = apagarNota;
  q('#znBusca').oninput = (e) => { busca = e.target.value; pintarLista(); };

  q('#znTitulo').addEventListener('input', aoDigitar);
  q('#znTitulo').addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); q('#znTexto').focus(); }
  });
  qq('.zn-texto').forEach(campo => {
    campo.addEventListener('input', () => { aoDigitar(); lembrarCursor(); });
    campo.addEventListener('keyup', lembrarCursor);
    campo.addEventListener('mouseup', lembrarCursor);
    campo.addEventListener('blur', pintarBotoesDeFormato);
    // colar sempre entra como texto: HTML de fora traz estilo que não é daqui
    campo.addEventListener('paste', (e) => {
      e.preventDefault();
      const t = (e.clipboardData || window.clipboardData).getData('text');
      document.execCommand('insertText', false, t);
    });
  });
  document.addEventListener('selectionchange', () => {
    if (raiz && raiz.isConnected) pintarBotoesDeFormato();
  });

  // segurar o mousedown: sem isso o clique tira o foco do texto antes do
  // comando rodar, e o negrito cai no vazio
  qq('.zn-ferramentas button').forEach(b => b.addEventListener('mousedown', e => e.preventDefault()));
  q('#znNegrito').onclick = () => formatar('bold');
  q('#znSublinhado').onclick = () => formatar('underline');
  q('#znLista').onclick = () => novoItem();
  q('#znFoto').onclick = () => { q('#znArquivo').value = ''; q('#znArquivo').click(); };
  q('#znPasta').onclick = escolherPasta;
  q('#znEtiqueta').onclick = novaEtiqueta;
  q('#znCor').onclick = () => q('#znPaleta').classList.toggle('on');
  q('#znArquivo').onchange = (e) => porFoto(e.target.files && e.target.files[0]);

  raiz.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { fecharGaveta(); return; }
    if (!(e.ctrlKey || e.metaKey)) return;
    if (e.key === 'n') { e.preventDefault(); novaNota(); }
    if (e.key === 'b') { e.preventDefault(); formatar('bold'); }
    if (e.key === 'u') { e.preventDefault(); formatar('underline'); }
  });

  pintarPaleta('');
  pintarLado();
  carregar();

  clearInterval(relogioSync);
  relogioSync = setInterval(() => { if (document.visibilityState === 'visible') carregar(true); }, 25000);
}

return { montar, recarregar: () => carregar(true), quantas: () => NOTAS.length };
})();
