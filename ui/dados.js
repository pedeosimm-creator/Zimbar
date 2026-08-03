/* ═══════════════════════════════════════════════════════════════
   DADOS — fala direto com o Supabase, os mesmos bancos que o site
   e o Zimbar antigo já usavam. Nada de localStorage pra conteúdo:
   o que fica na máquina é só preferência (tema, posição da janela).

   flowspace (fautswjwioiviqvpgsrw)
     app_kv  me2_foco   plano de hoje   {date, frog{text,done}, big[], med[], small[]}
     app_kv  me2_inbox  captura         [{id,text}]
     app_kv  me2_ritmo  ritmo da semana {items:[{id,text,days{},last,streak}]}
     app_kv  zimbar_ordem   ordem manual  {tarefas:[id], mural:[id]}
     tarefas       kanban + agenda   (status: a fazer|fazendo|feito, prazo: data)
     mural_items   listas            (categoria, texto)
     notas         ZimNotes          (titulo, corpo, cor)

   contas (abdlsipwqckuzaiuujcl)  meta / gastos_dia / compras / fixos
   ═══════════════════════════════════════════════════════════════ */

const FLOW = {
  url: 'https://fautswjwioiviqvpgsrw.supabase.co',
  key: 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImZhdXRzd2p3aW9pdmlxdnBnc3J3Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzk4ODkxNzgsImV4cCI6MjA5NTQ2NTE3OH0.99cWW-JA8-fH_wj_tqNLdpgubhM98UleH-0D5YaENM4'
};
const CONTAS = {
  url: 'https://abdlsipwqckuzaiuujcl.supabase.co',
  key: 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImFiZGxzaXB3cWNrdXphaXV1amNsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzk4MjA1ODQsImV4cCI6MjA5NTM5NjU4NH0.pJLcEncJ4nGcrH1KUPEDET9GM3krSWjlARUeaRGnYO0'
};

async function rest(banco, caminho, opc = {}) {
  const r = await fetch(banco.url + '/rest/v1/' + caminho, {
    ...opc,
    headers: {
      apikey: banco.key,
      Authorization: 'Bearer ' + banco.key,
      'Content-Type': 'application/json',
      ...(opc.headers || {})
    }
  });
  if (!r.ok) throw new Error(r.status + ' ' + caminho + ' ' + (await r.text()).slice(0, 180));
  const txt = await r.text();
  return txt ? JSON.parse(txt) : null;
}

const sel = (c) => rest(FLOW, c);
const ins = (t, linha) => rest(FLOW, t, { method: 'POST', body: JSON.stringify(linha) });
const upd = (t, filtro, patch) => rest(FLOW, `${t}?${filtro}`, { method: 'PATCH', body: JSON.stringify(patch) });
const del = (t, filtro) => rest(FLOW, `${t}?${filtro}`, { method: 'DELETE' });

async function kvLer(k) {
  const r = await sel(`app_kv?k=eq.${encodeURIComponent(k)}&select=v`);
  return r.length ? r[0].v : null;
}
async function kvGravar(k, v) {
  await rest(FLOW, 'app_kv', {
    method: 'POST',
    headers: { Prefer: 'resolution=merge-duplicates' },
    body: JSON.stringify({ k, v, updated_at: new Date().toISOString() })
  });
}
function kvJson(bruto, padrao) {
  if (!bruto) return padrao;
  try { return JSON.parse(bruto); } catch (e) { return padrao; }
}

/* ids no mesmo formato que o site e o Zimbar antigo geram */
function novoId(prefixo) {
  if (prefixo) return prefixo + Date.now() + Math.floor(Math.random() * 900 + 100);
  const chars = 'abcdefghijklmnopqrstuvwxyz0123456789';
  let s = '';
  for (let i = 0; i < 12; i++) s += chars[Math.floor(Math.random() * chars.length)];
  return s;
}

/* cores do ZimNotes: a chave é o que vai pro banco */
const CORES_NOTA = [
  { k: '',       hex: '#FFF7A8' }, { k: 'uva',   hex: '#E4D5FF' },
  { k: 'vinho',  hex: '#FFD0E8' }, { k: 'mel',   hex: '#FFE3A8' },
  { k: 'mata',   hex: '#CFF5DC' }, { k: 'noite', hex: '#D6EBFF' }
];
const corHex = (k) => (CORES_NOTA.find(c => c.k === (k || '')) || CORES_NOTA[0]).hex;

/* cor de cada lista, estável pelo nome da categoria */
const CORES_LISTA = ['#3b7bd4', '#c9a227', '#2f7d55', '#d4568a', '#7b5cd6', '#c2622d'];
function corLista(nome) {
  let h = 0;
  for (const ch of nome) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return CORES_LISTA[h % CORES_LISTA.length];
}

const hojeChave = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

/* ordena por uma lista de ids salva no banco; o que não estiver nela vai pro fim */
function porOrdem(itens, ordem) {
  const pos = new Map((ordem || []).map((id, i) => [id, i]));
  return itens.slice().sort((a, b) => {
    const pa = pos.has(a.id) ? pos.get(a.id) : 1e9;
    const pb = pos.has(b.id) ? pos.get(b.id) : 1e9;
    return pa !== pb ? pa - pb : String(a.criado || '').localeCompare(String(b.criado || ''));
  });
}

/* ═══════════════════════════════════════════════════════════════
   TRABALHO — a tabela `tasks` é do ConceWay, o site que o Pedro usa
   pra separar as coisas do trabalho. Mesmo banco, outra tabela.

   É COMPARTILHADA com a equipe (Bels, Livia, Dalc, Guru). Por isso
   TODA consulta e TODA gravação carregam `person=eq.Pedro`: um clique
   errado aqui não pode alcançar a tarefa de outra pessoa.
   ═══════════════════════════════════════════════════════════════ */
const DONO = 'Pedro';
const COLUNAS_TRABALHO = [
  { k: 'todo',  rot: 'A FAZER' },
  { k: 'doing', rot: 'FAZENDO' },
  { k: 'done',  rot: 'FEITO' }
];

const Dados = {
  CORES_NOTA, corHex, corLista, novoId, hojeChave, porOrdem,
  DONO, COLUNAS_TRABALHO,

  /* ═══ TRABALHO (ConceWay) ═══ */
  async trabalho() {
    return sel('tasks?select=id,title,description,area,person,status,start_date,deadline,due_time' +
               `&person=eq.${encodeURIComponent(DONO)}&order=deadline.asc.nullslast&limit=300`);
  },

  /* Move o card de coluna. O filtro por dono vai na URL de novo, de
     propósito: se o id vier errado por algum motivo, a linha de outra
     pessoa continua fora de alcance. */
  async moverTask(id, status) {
    if (!COLUNAS_TRABALHO.some(c => c.k === status)) throw new Error('status inválido: ' + status);
    return rest(FLOW, `tasks?id=eq.${encodeURIComponent(id)}&person=eq.${encodeURIComponent(DONO)}`, {
      method: 'PATCH',
      headers: { Prefer: 'return=representation' },
      body: JSON.stringify({ status })
    });
  },

  /* ═══ LEITURA INICIAL ═══ */
  async carregar() {
    const [foco, inbox, ritmo, ordem, tarefas, mural, notas] = await Promise.all([
      kvLer('me2_foco'), kvLer('me2_inbox'), kvLer('me2_ritmo'), kvLer('zimbar_ordem'),
      sel('tarefas?select=id,titulo,status,prazo,created_at&order=created_at.desc&limit=400'),
      sel('mural_items?select=id,categoria,texto,created_at&order=created_at.asc'),
      sel('notas?select=id,titulo,corpo,cor,created_at,pasta&order=created_at.desc&limit=200')
    ]);

    const f = kvJson(foco, { date: hojeChave(), frog: { text: '', done: false }, big: [], med: [], small: [] });
    const ord = kvJson(ordem, { tarefas: [], mural: [], listas: [] });

    // plano de hoje: big/med/small viram uma lista só com o nível marcado
    const nivelDe = { big: 'h', med: 'm', small: 'l' };
    const plano = [];
    for (const arr of ['big', 'med', 'small'])
      for (const it of (f[arr] || []))
        plano.push({ id: it.id || novoId(), t: it.text || '', n: nivelDe[arr], f: !!it.done, origem: it.origem || null });

    return {
      hoje: plano,
      frog: f.frog || { text: '', done: false },
      captura: kvJson(inbox, []).map(x => ({ id: x.id || novoId(), t: x.text || '' })),
      ritmo: kvJson(ritmo, { items: [] }),
      ordem: { tarefas: ord.tarefas || [], mural: ord.mural || [], listas: ord.listas || [] },
      tarefas: porOrdem(
        tarefas.map(t => ({
          id: t.id, t: t.titulo || '', status: t.status || 'a fazer',
          prazo: t.prazo || null, criado: t.created_at
        })), ord.tarefas),
      mural: porOrdem(
        mural.map(m => ({ id: m.id, cat: m.categoria || 'geral', t: m.texto || '', criado: m.created_at })),
        ord.mural),
      notas: notas.map(n => ({ id: n.id, t: n.titulo || '', b: n.corpo || '', c: n.cor || '', pasta: n.pasta || '' }))
    };
  },

  /* ═══ CHAVE/VALOR ═══ */
  gravarFoco(plano, frog) {
    const arrDe = { h: 'big', m: 'med', l: 'small' };
    const f = { date: hojeChave(), frog: frog || { text: '', done: false }, big: [], med: [], small: [] };
    for (const t of plano) f[arrDe[t.n] || 'small'].push({ id: t.id, text: t.t, done: !!t.f, origem: t.origem || null });
    return kvGravar('me2_foco', JSON.stringify(f));
  },
  gravarCaptura(lista) {
    return kvGravar('me2_inbox', JSON.stringify(lista.map(c => ({ id: c.id, text: c.t }))));
  },
  gravarRitmo(ritmo) { return kvGravar('me2_ritmo', JSON.stringify(ritmo)); },
  gravarOrdem(ordem) { return kvGravar('zimbar_ordem', JSON.stringify(ordem)); },

  /* ═══ TAREFAS (kanban + agenda) ═══ */
  criarTarefa(t) {
    return ins('tarefas', { id: t.id, titulo: t.t, status: t.status || 'a fazer', prazo: t.prazo || null, descricao: '' });
  },
  atualizarTarefa(t) {
    return upd('tarefas', 'id=eq.' + encodeURIComponent(t.id),
      { titulo: t.t, status: t.status || 'a fazer', prazo: t.prazo || null });
  },
  apagarTarefa(id) { return del('tarefas', 'id=eq.' + encodeURIComponent(id)); },

  /* ═══ LISTAS ═══ */
  criarItem(m) { return ins('mural_items', { id: m.id, categoria: m.cat, texto: m.t }); },
  atualizarItem(m) {
    return upd('mural_items', 'id=eq.' + encodeURIComponent(m.id), { categoria: m.cat, texto: m.t });
  },
  apagarItem(id) { return del('mural_items', 'id=eq.' + encodeURIComponent(id)); },

  /* ═══ NOTAS ═══ */
  criarNota(n) {
    return ins('notas', { id: n.id, titulo: n.t, corpo: n.b, cor: n.c || '', data_nota: hojeChave() });
  },
  atualizarNota(n) {
    return upd('notas', 'id=eq.' + encodeURIComponent(n.id), { titulo: n.t, corpo: n.b, cor: n.c || '' });
  },
  apagarNota(id) { return del('notas', 'id=eq.' + encodeURIComponent(id)); },
  /* o rol de pastas do ZimNotes (mesma chave que o celular usa) */
  async pastasNota() {
    try {
      const r = await sel('app_kv?k=eq.zimnotes_pastas&select=v');
      return r.length ? JSON.parse(r[0].v) : [];
    } catch (e) { return []; }
  },

  /* ═══ CONTAS (banco separado) ═══ */
  async contas() {
    const q = (c) => rest(CONTAS, c);
    const [meta, gastos, compras, fixos] = await Promise.all([
      q('meta?select=*&limit=1'),
      q('gastos_dia?select=*&order=dia.desc,criado_em.desc&limit=400'),
      q('compras?select=*&order=id.asc'),
      q('fixos?select=*&order=id.asc')
    ]);
    return { meta: meta[0] || null, gastos, compras, fixos };
  },
  atualizarConta(saldo, fatura) {
    return rest(CONTAS, 'meta?id=eq.1', {
      method: 'PATCH',
      body: JSON.stringify({ saldo_conta: saldo, fatura, atualizado_em: new Date().toISOString() })
    });
  }
};
