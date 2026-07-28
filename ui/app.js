
/* ═══════════════════════════════════════════════════════════════
   ESTADO — vive no Supabase. O que fica na máquina é só gosto:
   tema. Conteúdo nenhum.

   S guarda o que está na tela; salvar() compara com o que veio do
   banco e manda só a diferença. Assim toda a interface continua
   fazendo "mexe no S e chama salvar()", sem saber de REST.
   ═══════════════════════════════════════════════════════════════ */
function chave(d){ return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`; }
function maisDias(n){ const d=new Date(); d.setDate(d.getDate()+n); return chave(d); }
function dLabel(iso){
  if(!iso) return null;
  const [a,m,d]=iso.split('-');
  return d+'/'+m;
}

const COLUNAS=['a fazer','fazendo','feito'];

let S = {
  tema: localStorage.getItem('zimbar-tema') || 'claro',
  hoje: [], frog:{text:'',done:false},
  captura: [], ritmo:{items:[]},
  tarefas: [], mural: [], notas: [],
  ordem: {tarefas:[], mural:[]},
  conta: null,
  /* do ConceWay; fora do S/BASE de propósito — é tabela de outro app,
     compartilhada, e grava na hora em vez de entrar no diff */
  trabalho: [],
  topicos: JSON.parse(localStorage.getItem('zimbar-secoes')||'["cinema","fotografia"]'),
  secaoAtiva: localStorage.getItem('zimbar-secao') || 'destaques'
};

let BASE = null;        // retrato do que está no banco
let sincronizando=false, pendente=false, relogio=null;

const retrato = () => JSON.stringify({
  hoje:S.hoje, frog:S.frog, captura:S.captura, ritmo:S.ritmo,
  tarefas:S.tarefas, mural:S.mural, notas:S.notas, ordem:S.ordem
});

/* chamado pela interface inteira: agenda a sincronia */
function salvar(){
  localStorage.setItem('zimbar-tema', S.tema);
  localStorage.setItem('zimbar-secoes', JSON.stringify(S.topicos));
  localStorage.setItem('zimbar-secao', S.secaoAtiva);
  S.ordem.tarefas = S.tarefas.map(t=>t.id);
  S.ordem.mural   = S.mural.map(m=>m.id);
  S.ordem.listas  = categorias();
  clearTimeout(relogio);
  relogio=setTimeout(sincronizar, 400);
}

/* manda só o que mudou */
async function sincronizar(){
  if(!BASE) return;
  if(sincronizando){ pendente=true; return; }
  sincronizando=true;
  sinal('salvando');
  const antes=JSON.parse(BASE), agora=JSON.parse(retrato());
  const tarefas=[];
  const igual=(a,b)=>JSON.stringify(a)===JSON.stringify(b);

  try{
    if(!igual(antes.hoje,agora.hoje)||!igual(antes.frog,agora.frog))
      tarefas.push(Dados.gravarFoco(agora.hoje, agora.frog));
    if(!igual(antes.captura,agora.captura)) tarefas.push(Dados.gravarCaptura(agora.captura));
    if(!igual(antes.ritmo,agora.ritmo))     tarefas.push(Dados.gravarRitmo(agora.ritmo));
    if(!igual(antes.ordem,agora.ordem))     tarefas.push(Dados.gravarOrdem(agora.ordem));

    diferenca(antes.tarefas, agora.tarefas, Dados.criarTarefa, Dados.atualizarTarefa, Dados.apagarTarefa, tarefas);
    diferenca(antes.mural,   agora.mural,   Dados.criarItem,   Dados.atualizarItem,   Dados.apagarItem,   tarefas);
    diferenca(antes.notas,   agora.notas,   Dados.criarNota,   Dados.atualizarNota,   Dados.apagarNota,   tarefas);

    await Promise.all(tarefas);
    BASE=JSON.stringify(agora);
    sinal(tarefas.length?'salvo':'');
  }catch(err){
    console.error('sincronia',err);
    sinal('sem salvar', true);
  }finally{
    sincronizando=false;
    if(pendente){ pendente=false; setTimeout(sincronizar,150); }
  }
}

/* linhas novas, mudadas e apagadas de uma tabela */
function diferenca(antes, agora, criar, atualizar, apagar, fila){
  const mapaAntes=new Map(antes.map(x=>[x.id,x]));
  const mapaAgora=new Map(agora.map(x=>[x.id,x]));
  for(const item of agora){
    const velho=mapaAntes.get(item.id);
    if(!velho) fila.push(criar(item));
    else if(JSON.stringify(velho)!==JSON.stringify(item)) fila.push(atualizar(item));
  }
  for(const velho of antes)
    if(!mapaAgora.has(velho.id)) fila.push(apagar(velho.id));
}

/* aviso discreto de sincronia no canto */
let sinalT;
function sinal(txt, erro){
  const el=document.getElementById('sync'); if(!el) return;
  el.textContent=txt||'';
  el.classList.toggle('erro',!!erro);
  el.classList.toggle('on',!!txt);
  clearTimeout(sinalT);
  if(txt&&txt!=='salvando') sinalT=setTimeout(()=>el.classList.remove('on'), erro?4000:1400);
}

/* ═══ VISÕES DERIVADAS ═══
   tarefas é uma tabela só: o kanban corta por status, a agenda por prazo. */
const tarefasDa   = (status) => S.tarefas.filter(t=>(t.status||'a fazer')===status);
const tarefasDoDia= (k)      => S.tarefas.filter(t=>t.prazo===k);
const listaDe     = (cat)    => S.mural.filter(m=>m.cat===cat);
function categorias(){
  const vistas=[...(S.ordem.listas||[])];
  for(const m of S.mural) if(!vistas.includes(m.cat)) vistas.push(m.cat);
  return vistas;
}

/* ═══ NAVEGAÇÃO ═══ */
const ORDEM=['hoje','kanban','agenda','listas','noticias','contas','trabalho'];
function go(v){
  document.querySelectorAll('.view').forEach(x=>x.classList.remove('on'));
  document.getElementById('v-'+v).classList.add('on');
  document.querySelectorAll('.nv').forEach(b=>b.classList.toggle('on',b.dataset.v===v));
}
document.querySelectorAll('.nv').forEach(b=>b.onclick=()=>go(b.dataset.v));
document.addEventListener('keydown',e=>{
  if(e.ctrlKey&&e.key==='k'){e.preventDefault();document.getElementById('capIn').focus();return;}
  if(e.target.tagName==='INPUT')return;
  const i=parseInt(e.key)-1;
  if(i>=0&&i<ORDEM.length)go(ORDEM[i]);
});

/* ═══ TEMA ═══ */
function aplicaTema(){
  document.documentElement.dataset.theme = S.tema==='escuro'?'dark':'';
  document.getElementById('themeVal').textContent = S.tema;
}
document.getElementById('themeBtn').onclick=()=>{ S.tema = S.tema==='claro'?'escuro':'claro'; aplicaTema(); salvar(); };

/* ═══ SAUDAÇÃO ═══ */
(function(){
  const h=new Date().getHours();
  const s=h<6?'Boa madrugada':h<12?'Bom dia':h<18?'Boa tarde':'Boa noite';
  document.getElementById('saud').innerHTML=s+', <em>Pedro.</em><small>'+
    new Date().toLocaleDateString('pt-BR',{weekday:'long',day:'2-digit',month:'long'}).toUpperCase()+'</small>';
})();

/* ═══════════════════════════════════════════════════════════════
   ARRASTAR — padrão do sistema inteiro.
   Todo item de lista é arrastável: reordena dentro do grupo e move
   entre grupos. Puxou = arrastou; soltou sem puxar = abriu o item.
   ═══════════════════════════════════════════════════════════════ */
const LIMIAR=5;    // px antes de virar arrasto (abaixo disso é clique)
const BORDA=34;    // px da borda da zona onde a rolagem automática liga

function arrastavel(el,cfg){
  // cfg = {zona, indice, carga, aoSoltar(zonaDestino, indiceDestino), aoAbrir()}
  //   carga = {texto, tirar()}  -> permite soltar o item nos MÓDULOS do trilho
  el.classList.add('arr');
  el.dataset.dndItem='1';
  el.dataset.dndZona=cfg.zona;
  el.dataset.dndIdx=cfg.indice;

  el.addEventListener('mousedown',e=>{
    if(e.button!==0) return;
    if(e.target.closest('button,input,textarea,select,.bx,.pin,.del,.cores')) return;
    const x0=e.clientX, y0=e.clientY;
    let puxou=false, fantasma=null, marca=null, alvo=null, dica=el.title;
    let quadro=0, ux=0, uy=0;   // o fantasma segue o mouse; a conta do alvo é por quadro

    const posicionar=(x,y)=>{
      fantasma.style.transform=
        'translate3d('+(x-fantasma._dx)+'px,'+(y-fantasma._dy)+'px,0) rotate(-1.2deg)';
    };
    const conta=()=>{ quadro=0; alvo=calcularAlvo(ux,uy,el,marca); };

    const mover=ev=>{
      if(!puxou){
        if(Math.hypot(ev.clientX-x0,ev.clientY-y0)<LIMIAR) return;
        puxou=true;
        document.body.classList.add('arrastando');
        el.title='';                       // sem tooltip nativo no meio do arrasto
        const r=el.getBoundingClientRect();
        fantasma=el.cloneNode(true);
        fantasma.className=(el.className.replace('arr','')+' fantasma').trim();
        fantasma.style.width=r.width+'px';
        fantasma.style.height=r.height+'px';
        fantasma.style.background=getComputedStyle(el).backgroundColor;
        fantasma._dx=x0-r.left; fantasma._dy=y0-r.top;
        posicionar(x0,y0);
        document.body.appendChild(fantasma);
        el.classList.add('fonte');
        marca=document.createElement('div'); marca.className='marcador';
        document.body.appendChild(marca);
      }
      posicionar(ev.clientX,ev.clientY);
      ux=ev.clientX; uy=ev.clientY;
      if(!quadro) quadro=requestAnimationFrame(conta);
    };
    const soltar=ev=>{
      if(quadro){ cancelAnimationFrame(quadro); quadro=0;
                  if(puxou) alvo=calcularAlvo(ev.clientX,ev.clientY,el,marca); }
      document.removeEventListener('mousemove',mover);
      document.removeEventListener('mouseup',soltar);
      document.body.classList.remove('arrastando');
      limparRealce();
      fantasma?.remove(); marca?.remove();
      el.classList.remove('fonte'); el.title=dica;
      if(!puxou){ cfg.aoAbrir?.(); return; }
      if(!alvo) return;
      if(alvo.zona.startsWith('rail:')){          // soltou num módulo do trilho
        if(cfg.carga) mandarPara(alvo.zona.slice(5),cfg.carga,ev.clientX,ev.clientY);
        else toast('esse item não vai pra lá');
        return;
      }
      cfg.aoSoltar?.(alvo.zona,alvo.indice);
    };
    document.addEventListener('mousemove',mover);
    document.addEventListener('mouseup',soltar);
    e.preventDefault();
  });
}

/* só mexe nas classes quando a zona MUDA — repintar a coluna inteira a
   cada movimento do mouse é o que fazia o arrasto engasgar */
let _realce=null;
function limparRealce(){
  document.querySelectorAll('.zona-ativa,.alvo-rail')
          .forEach(z=>z.classList.remove('zona-ativa','alvo-rail'));
  _realce=null;
}
function realcar(el,classe){
  if(_realce===el) return;
  if(_realce) _realce.classList.remove('zona-ativa','alvo-rail');
  el.classList.add(classe);
  _realce=el;
}

/* acha a zona sob o cursor. Se o ponto exato cair num vão (borda do card,
   cabeçalho, espaço entre colunas), procura a zona mais próxima — sem isso
   o arrasto "morre" em qualquer pixel que não seja item. */
function acharZona(x,y){
  const sob=document.elementFromPoint(x,y);
  const direto=sob?.closest('[data-dnd-zona-nome]');
  if(direto) return direto;
  let melhor=null, dist=Infinity;
  document.querySelectorAll('[data-dnd-zona-nome]').forEach(z=>{
    if(!z.offsetParent && z.offsetWidth===0) return;
    const r=z.getBoundingClientRect();
    if(r.width===0||r.height===0) return;
    const dx=Math.max(r.left-x,0,x-r.right), dy=Math.max(r.top-y,0,y-r.bottom);
    const d=Math.hypot(dx,dy);
    if(d<dist){ dist=d; melhor=z; }
  });
  return dist<=28?melhor:null;
}

/* rola sozinho quando o cursor encosta na borda de uma área com rolagem */
function rolarPerto(zonaEl,y){
  const cx=zonaEl.matches('.scrollbox')?zonaEl:zonaEl.querySelector('.scrollbox')||zonaEl.closest('.scrollbox');
  if(!cx||cx.scrollHeight<=cx.clientHeight+2) return;
  const r=cx.getBoundingClientRect();
  if(y<r.top+BORDA)         cx.scrollTop-=14;
  else if(y>r.bottom-BORDA) cx.scrollTop+=14;
}

/* descobre em qual zona e entre quais itens o cursor está.
   Funciona em coluna, em linha e em grade: pega o item de centro mais
   perto e decide antes/depois pelo eixo que domina naquela zona. */
function calcularAlvo(x,y,origem,marca){
  const zonaEl=acharZona(x,y);
  if(!zonaEl){ limparRealce(); marca.style.display='none'; return null; }
  const zona=zonaEl.dataset.dndZonaNome;

  if(zona.startsWith('rail:')){               // módulo do trilho: sem marcador
    realcar(zonaEl,'alvo-rail');
    marca.style.display='none';
    return {zona,indice:0};
  }
  realcar(zonaEl,'zona-ativa');
  rolarPerto(zonaEl,y);
  marca.style.display='';

  const itens=[...zonaEl.querySelectorAll('[data-dnd-item]')].filter(i=>i!==origem);
  const rz=zonaEl.getBoundingClientRect();
  if(!itens.length){
    marca.style.cssText+=';';
    marca.style.top=(rz.top+10)+'px'; marca.style.left=(rz.left+12)+'px';
    marca.style.width=Math.max(rz.width-24,20)+'px'; marca.style.height='2.5px';
    return {zona,indice:0};
  }

  // eixo dominante: dois itens lado a lado na mesma faixa vertical => linha/grade
  let emLinha=false;
  for(let i=1;i<itens.length&&!emLinha;i++){
    const a=itens[i-1].getBoundingClientRect(), b=itens[i].getBoundingClientRect();
    if(b.left>=a.right-2 && b.top<a.bottom-2) emLinha=true;
  }

  let melhor=0, dist=Infinity;
  itens.forEach((it,i)=>{
    const r=it.getBoundingClientRect();
    const d=Math.hypot(x-(r.left+r.width/2), y-(r.top+r.height/2));
    if(d<dist){ dist=d; melhor=i; }
  });
  const r=itens[melhor].getBoundingClientRect();
  const depois = emLinha ? x > r.left+r.width/2 : y > r.top+r.height/2;
  const indice = melhor + (depois?1:0);

  if(emLinha){
    marca.style.left=((depois?r.right:r.left)-1)+'px';
    marca.style.top=r.top+'px'; marca.style.width='2.5px'; marca.style.height=r.height+'px';
  } else {
    marca.style.left=r.left+'px'; marca.style.width=r.width+'px'; marca.style.height='2.5px';
    marca.style.top=((depois?r.bottom+2:r.top-4))+'px';
  }
  return {zona,indice};
}
function zonaSoltar(el,nome){ el.dataset.dndZonaNome=nome; return el; }

/* ═══════════════════════════════════════════════════════════════
   MANDAR PRO MÓDULO — soltar qualquer item em cima de um módulo do
   trilho joga ele lá dentro. A captura é caixa de entrada: sai de lá.
   O resto é acervo: fica onde estava e vira uma cópia no destino.
   ═══════════════════════════════════════════════════════════════ */
function noPonto(x,y){ return {getBoundingClientRect:()=>
  ({top:y,bottom:y,left:x,right:x,width:0,height:0})}; }

function renderTudo(){
  [renderHoje,renderKanban,renderCaptura,renderAgenda,renderEventos,
   renderProximos,renderTopicos,renderFeed,atualizaContadorNotas]
    .forEach(f=>{ try{ f(); }catch(e){} });
  try{ renderListas(); }catch(e){}
}

const novaTarefa=(texto,status,prazo)=>
  ({id:Dados.novoId('t'),t:texto,status:status||'a fazer',prazo:prazo||null,criado:new Date().toISOString()});
function novaNotaCom(texto){
  const n={id:Dados.novoId(),
           t:texto.length>42?texto.slice(0,42)+'…':texto,
           c:Dados.CORES_NOTA[S.notas.length%Dados.CORES_NOTA.length].k,
           b:texto};
  S.notas.unshift(n);
  return n;
}

function mandarPara(destino,carga,x,y){
  const a=noPonto(x,y), txt=carga.texto;
  const feito=m=>{ carga.tirar?.(); salvar(); renderTudo(); toast(m); };

  if(destino==='hoje'){
    menu(a,'PRO PLANO COMO',NIVEIS.map(n=>({rot:n.un,cor:n.cor,
      go:()=>{ S.hoje.push({id:Dados.novoId(),t:txt,n:n.k,f:false});
               feito('foi pro plano de hoje'); }})));
    return;
  }
  if(destino==='kanban'){
    menu(a,'EM QUAL COLUNA',COLUNAS.map((status,ci)=>({rot:status,cor:corCol(ci),
      go:()=>{ S.tarefas.unshift(novaTarefa(txt,status)); feito('virou card no kanban'); }})));
    return;
  }
  if(destino==='agenda'){
    menu(a,'PRA QUANDO',[0,1,2,7].map(d=>({cor:'var(--warn)',
      rot:d===0?'hoje':d===1?'amanhã':'em '+d+' dias',
      go:()=>{ S.tarefas.unshift(novaTarefa(txt,'a fazer',maisDias(d)));
               feito('marcado na agenda'); }})));
    return;
  }
  if(destino==='listas'){
    const cats=categorias();
    menu(a,'EM QUAL LISTA',cats.map(c=>({rot:c,cor:Dados.corLista(c),
      go:()=>{ S.mural.push({id:Dados.novoId('m'),cat:c,t:txt,criado:new Date().toISOString()});
               feito('foi pra lista '+c); }})));
    return;
  }
  if(destino==='notas'){
    const n=novaNotaCom(txt);
    carga.tirar?.(); salvar(); renderTudo();
    abrirSticky(n.id); toast('virou nota');
    return;
  }
  toast('esse módulo não recebe itens');
}

/* ═══ EDITOR DE ITEM ═══ */
function editor({titulo,campos,acoes,aoSalvar,aoApagar}){
  const veu=document.createElement('div'); veu.className='veu';
  const dlg=document.createElement('div'); dlg.className='dlg';
  dlg.innerHTML=`<div class="dh"><h3>${titulo}</h3><div class="sp"></div><button class="fechar">✕</button></div>
    <div class="db"></div>
    <div class="df">${aoApagar?'<button class="apagar">apagar</button>':''}
      ${(acoes||[]).map((a,i)=>`<button class="extra" data-i="${i}">${esc(a.rot)}</button>`).join('')}
      <div class="sp"></div><button class="salvar">salvar</button></div>`;
  const corpo=dlg.querySelector('.db');
  const vals={};
  campos.forEach(c=>{
    const w=document.createElement('div'); w.className='campo';
    w.innerHTML=`<label>${c.rot}</label>`;
    if(c.tipo==='opcoes'){
      const g=document.createElement('div'); g.className='opcs';
      vals[c.id]=c.valor;
      c.opcoes.forEach(o=>{
        const b=document.createElement('button');
        b.innerHTML=(o.cor?`<i style="background:${o.cor}"></i>`:'')+esc(o.rot);
        b.className=(o.v===c.valor?'on':'');
        b.onclick=()=>{ vals[c.id]=o.v; g.querySelectorAll('button').forEach(x=>x.classList.toggle('on',x===b)); };
        g.appendChild(b);
      });
      w.appendChild(g);
    } else {
      const inp=document.createElement(c.tipo==='texto'?'textarea':'input');
      if(c.tipo==='data'){ inp.type='date'; inp.className='dt'; }
      inp.value=c.valor||''; if(c.dica) inp.placeholder=c.dica;
      inp.oninput=()=>vals[c.id]=inp.value; vals[c.id]=c.valor||'';
      if(c.tipo!=='texto') inp.onkeydown=e=>{ if(e.key==='Enter') dlg.querySelector('.salvar').click(); };
      w.appendChild(inp);
    }
    corpo.appendChild(w);
  });
  const fim=()=>veu.remove();
  dlg.querySelector('.fechar').onclick=fim;
  veu.onclick=e=>{ if(e.target===veu) fim(); };
  document.addEventListener('keydown',function esc2(e){
    if(e.key==='Escape'){ fim(); document.removeEventListener('keydown',esc2); } });
  dlg.querySelector('.salvar').onclick=()=>{ aoSalvar(vals); fim(); };
  dlg.querySelector('.apagar')?.addEventListener('click',()=>{ aoApagar(); fim(); });
  // guarda a posição do botão antes de fechar: o menuzinho abre ali mesmo
  dlg.querySelectorAll('.extra').forEach(b=>b.onclick=()=>{
    const a=acoes[+b.dataset.i], r=b.getBoundingClientRect();
    fim(); a.go(noPonto(r.left,r.bottom));
  });
  veu.appendChild(dlg); document.body.appendChild(veu);
  setTimeout(()=>corpo.querySelector('input,textarea')?.focus(),50);
}

/* ═══ HOJE ═══ */
const NIVEIS=[
  {k:'h',rot:'DIFÍCEIS',un:'difícil',cor:'var(--acc)'},
  {k:'m',rot:'MÉDIAS',  un:'média',  cor:'var(--warn)'},
  {k:'l',rot:'FÁCEIS',  un:'fácil',  cor:'var(--ok)'}
];
/* Foco = a tarefa fixada por você (alfinete). Sem nenhuma fixada, cai na
   regra automática: primeira DIFÍCIL em aberto; se não houver, primeira em aberto. */
const ehFoco=(t)=>((S.frog&&S.frog.text)||'').trim()===t.t.trim()&&t.t.trim()!=='';
function fixarFoco(t){ S.frog = ehFoco(t) ? {text:'',done:false} : {text:t.t,done:false}; }
function acharFoco(){
  const fixado=((S.frog&&S.frog.text)||'').trim();
  if(fixado){
    const fx=S.hoje.find(t=>!t.f&&t.t.trim()===fixado);
    if(fx) return {t:fx,fixo:true};
  }
  const a=S.hoje.find(t=>!t.f&&t.n==='h')||S.hoje.find(t=>!t.f);
  return a?{t:a,fixo:false}:null;
}
function renderHoje(){
  const box=document.getElementById('listaHoje'); box.innerHTML='';
  const foco=acharFoco();
  if(!S.hoje.length) box.innerHTML='<div class="empty">nada planejado — escreve abaixo</div>';

  NIVEIS.forEach(nv=>{
    const itens=S.hoje.filter(t=>t.n===nv.k);
    if(!itens.length&&!S.hoje.length) return;
    // o bloco inteiro (cabeçalho + tarefas) é a zona: soltar em qualquer
    // parte dele muda a dificuldade, não só em cima das tarefas
    const bloco=document.createElement('div'); bloco.className='blnv';
    zonaSoltar(bloco,'niv'+nv.k);
    box.appendChild(bloco);
    const cab=document.createElement('div'); cab.className='nivel';
    const abertas=itens.filter(t=>!t.f).length;
    cab.innerHTML=`<i style="background:${nv.cor}"></i><h4>${nv.rot}</h4><div class="risco"></div>
      <span class="qt">${itens.length?abertas+'/'+itens.length:'—'}</span>`;
    bloco.appendChild(cab);
    const grupo=document.createElement('div');
    grupo.style.minHeight='26px';
    bloco.appendChild(grupo);
    if(!itens.length){
      grupo.innerHTML='<div class="empty" style="padding:2px 6px 4px;font-size:11.5px">arrasta uma tarefa pra cá</div>';
      return;
    }
    itens.forEach((t,ti)=>{
      const el=document.createElement('div');
      el.className='tk'+(t.f?' done':'')+(foco&&foco.t.id===t.id?' foco':'');
      el.innerHTML=`<div class="bx" title="marcar como feito">✓</div><div class="tx">${esc(t.t)}</div>
        <button class="pin${ehFoco(t)?' on':''}" title="fixar como foco do dia">⌖</button>
        <button class="del" title="apagar">✕</button>`;
      el.querySelector('.bx').onclick=ev=>{ ev.stopPropagation(); t.f=!t.f; salvar(); renderHoje(); };
      el.querySelector('.pin').onclick=ev=>{ ev.stopPropagation();
        fixarFoco(t); salvar(); renderHoje(); };
      el.querySelector('.del').onclick=ev=>{ ev.stopPropagation();
        if(ehFoco(t)) S.frog={text:'',done:false};
        S.hoje=S.hoje.filter(x=>x.id!==t.id); salvar(); renderHoje(); };
      el.title='arrasta pra mudar o nível · solta num módulo do trilho pra mandar pra lá · clica pra abrir';
      arrastavel(el,{zona:'niv'+nv.k,indice:ti,
        carga:{texto:t.t},
        aoSoltar:(zona)=>{
          if(!zona.startsWith('niv')) return;
          const novo=zona.slice(3);
          if(novo!==t.n){ t.n=novo; salvar(); renderHoje(); toast('agora é '+NIVEIS.find(x=>x.k===novo).un); }
        },
        aoAbrir:()=>abrirTarefa(t.id)});
      grupo.appendChild(el);
    });
  });

  const falta=S.hoje.filter(t=>!t.f).length;
  document.getElementById('planoCount').textContent=falta+' aberta'+(falta===1?'':'s');
  document.getElementById('ct-hoje').textContent=falta||'';
  document.getElementById('focoTxt').textContent=foco?foco.t.t:'Tudo feito. Respira.';
  document.getElementById('focoOrigem').textContent=
    !foco?'FOCO DO DIA':foco.fixo?'FOCO DO DIA · FIXADO POR VOCÊ':
    foco.t.n==='h'?'FOCO DO DIA · A DIFÍCIL MAIS ANTIGA':'FOCO DO DIA · PRÓXIMA EM ABERTO';
  document.getElementById('btnSoltar').style.display=(foco&&foco.fixo)?'':'none';
}
function abrirTarefa(id){
  const t=S.hoje.find(x=>x.id===id); if(!t) return;
  editor({titulo:'TAREFA DE HOJE',campos:[
      {id:'t',rot:'TAREFA',tipo:'linha',valor:t.t},
      {id:'n',rot:'NÍVEL',tipo:'opcoes',valor:t.n,
       opcoes:NIVEIS.map(n=>({v:n.k,rot:n.un,cor:n.cor}))},
      {id:'f',rot:'SITUAÇÃO',tipo:'opcoes',valor:t.f,
       opcoes:[{v:false,rot:'em aberto',cor:'var(--warn)'},{v:true,rot:'feita',cor:'var(--ok)'}]}
    ],
    aoSalvar:v=>{ t.t=v.t.trim()||t.t; t.n=v.n; t.f=v.f; salvar(); renderHoje(); toast('tarefa salva'); },
    aoApagar:()=>{ if(ehFoco(t)) S.frog={text:'',done:false};
                   S.hoje=S.hoje.filter(x=>x.id!==id);
                   salvar(); renderHoje(); toast('tarefa apagada'); }});
}
function concluirFoco(){
  const f=acharFoco();
  if(f){ f.t.f=true; if(f.fixo) S.frog={text:'',done:true}; salvar(); renderHoje(); toast('feito ✓'); }
}
function soltarFoco(){ S.frog={text:'',done:false}; salvar(); renderHoje(); toast('voltou pro automático'); }
let nivNovo='l';
document.querySelectorAll('#nivSel b').forEach(b=>b.onclick=()=>{
  nivNovo=b.dataset.n;
  document.querySelectorAll('#nivSel b').forEach(x=>x.classList.toggle('on',x===b));
});
document.getElementById('addHoje').onkeydown=e=>{
  if(e.key==='Enter'&&e.target.value.trim()){
    S.hoje.push({id:Dados.novoId(),t:e.target.value.trim(),n:nivNovo,f:false});
    e.target.value=''; salvar(); renderHoje();
  }
};
const DIAS=['S','T','Q','Q','S','S','D'];
function hojeIdx(){ return (new Date().getDay()+6)%7; }
/* as datas da semana corrente, de segunda a domingo */
function semanaAtual(){
  const d=new Date(); d.setHours(12,0,0,0);
  d.setDate(d.getDate()-((d.getDay()+6)%7));
  return Array.from({length:7},(_,i)=>{ const x=new Date(d); x.setDate(d.getDate()+i); return chave(x); });
}
function renderRitmo(){
  const box=document.getElementById('ritmoHm'); box.innerHTML='';
  const hi=hojeIdx(), semana=semanaAtual();
  const itens=(S.ritmo&&S.ritmo.items)||[];
  // a régua de dias é toda .hd — assim o modo simples esconde a linha inteira
  box.appendChild(Object.assign(document.createElement('div'),{className:'hd'}));
  DIAS.forEach((d,i)=>{ const e=document.createElement('div'); e.className='hd';
    e.textContent=d; if(i===hi) e.style.color='var(--acc)'; box.appendChild(e); });
  itens.forEach(r=>{
    r.days ||= {};
    const n=document.createElement('div'); n.className='hn'; n.textContent=r.text||''; box.appendChild(n);
    semana.forEach((iso,i)=>{
      const c=document.createElement('div');
      c.className='cell'+(r.days[iso]?' f':'')+(i===hi?' hj':'');
      c.title=(r.text||'')+' · '+['segunda','terça','quarta','quinta','sexta','sábado','domingo'][i];
      c.onclick=()=>{ if(r.days[iso]) delete r.days[iso]; else r.days[iso]=true;
                      salvar(); renderRitmo(); };
      box.appendChild(c);
    });
  });
  if(!itens.length) box.innerHTML+='<div class="empty" style="grid-column:1/-1">sem hábitos ainda</div>';

  const hojeIso=chave(new Date());
  const feitosHoje=itens.filter(r=>r.days&&r.days[hojeIso]).length;
  let seq=0;                       // dias seguidos com pelo menos um hábito
  for(let i=0;i<400;i++){
    const d=new Date(); d.setDate(d.getDate()-i);
    if(itens.some(r=>r.days&&r.days[chave(d)])) seq++;
    else if(i>0) break;            // hoje ainda pode estar em branco
  }
  // no cabeçalho fica só a conta do dia; a sequência mora no tooltip
  const el=document.getElementById('streakBox');
  el.innerHTML=`<b>${feitosHoje}/${itens.length}</b> hoje`;
  el.title=`${feitosHoje} de ${itens.length} hábitos feitos hoje · `+
           `${seq} ${seq===1?'dia seguido':'dias seguidos'}`;
}
function renderCaptura(){
  const box=document.getElementById('listaCap'); box.innerHTML='';
  zonaSoltar(box,'cap');   // registra antes: a caixa vazia também recebe
  if(!S.captura.length){box.innerHTML='<div class="empty">nada capturado — tudo decidido</div>';return;}
  S.captura.forEach((c,i)=>{
    const el=document.createElement('div'); el.className='cap-item';
    el.innerHTML=`<div class="ctx">${esc(c.t)}</div>
      <div class="envios">
        <button data-d="hoje">◈ hoje</button>
        <button data-d="kanban">◱ kanban</button>
        <button data-d="agenda">◷ agenda</button>
        <button data-d="lista">☰ lista</button>
        <button data-d="nota">✎ nota</button>
        <button data-d="apagar" class="x" title="descartar">✕</button>
      </div>`;
    el.querySelectorAll('.envios button').forEach(b=>{
      b.onclick=ev=>{ ev.stopPropagation(); enviarCaptura(c.id,b.dataset.d,b); };
    });
    el.title='arrasta pro plano/kanban/lista ou pra um módulo do trilho · clica pra editar';
    const tira=()=>{ S.captura=S.captura.filter(x=>x.id!==c.id); };
    arrastavel(el,{zona:'cap',indice:i,
      carga:{texto:c.t, tirar:tira},   // caixa de entrada: esvazia
      aoSoltar:(zona,idx)=>{
        if(zona==='cap'){ const [m]=S.captura.splice(i,1); S.captura.splice(idx,0,m); salvar(); renderCaptura(); return; }
        if(zona.startsWith('niv')){
          S.hoje.push({id:Dados.novoId(),t:c.t,n:zona.slice(3),f:false});
          tira(); salvar(); renderCaptura(); renderHoje(); toast('virou tarefa de hoje'); return;
        }
        if(zona.startsWith('kb')){
          const status=COLUNAS[parseInt(zona.slice(2))];
          const nova=novaTarefa(c.t,status);
          S.tarefas.push(nova); recolocar(nova, tarefasDa(status)[idx]);
          tira(); salvar(); renderCaptura(); renderKanban(); toast('virou card no kanban'); return;
        }
        if(zona.startsWith('lst')){
          const cat=categorias()[parseInt(zona.slice(3))];
          S.mural.push({id:Dados.novoId('m'),cat,t:c.t,criado:new Date().toISOString()});
          tira(); salvar(); renderCaptura(); renderListas(); toast('foi pra lista '+cat); return;
        }
        if(zona.startsWith('dia:')){
          S.tarefas.unshift(novaTarefa(c.t,'a fazer',zona.slice(4)));
          tira(); salvar(); renderCaptura();
          renderAgenda(); renderEventos(); renderProximos(); toast('marcado na agenda'); return;
        }
      },
      aoAbrir:()=>editor({titulo:'CAPTURA',campos:[{id:'t',rot:'TEXTO',tipo:'texto',valor:c.t}],
        aoSalvar:v=>{ const t=v.t.trim(); if(t){c.t=t; salvar(); renderCaptura();} },
        aoApagar:()=>{ tira(); salvar(); renderCaptura(); toast('descartado'); }})});
    box.appendChild(el);
  });
}
/* manda a captura pro módulo escolhido; quando precisa saber ONDE
   (qual lista, qual coluna, que dia), abre um menuzinho antes */
function enviarCaptura(id,destino,alvoBtn){
  const c=S.captura.find(x=>x.id===id); if(!c) return;
  const txt=c.t;
  const tira=()=>{ S.captura=S.captura.filter(x=>x.id!==id); salvar(); renderCaptura(); };

  if(destino==='apagar'){ tira(); toast('descartado'); return; }

  if(destino==='hoje'){
    menu(alvoBtn,'MANDAR COMO',NIVEIS.map(n=>({rot:n.un,cor:n.cor,
      go:()=>{ S.hoje.push({id:Dados.novoId(),t:txt,n:n.k,f:false}); tira(); renderHoje(); toast('virou tarefa de hoje'); }})));
    return;
  }
  if(destino==='kanban'){
    menu(alvoBtn,'EM QUAL COLUNA',COLUNAS.map((status,ci)=>({rot:status,cor:corCol(ci),
      go:()=>{ S.tarefas.unshift(novaTarefa(txt,status)); tira(); renderKanban(); toast('virou card no kanban'); }})));
    return;
  }
  if(destino==='lista'){
    menu(alvoBtn,'EM QUAL LISTA',categorias().map(cat=>({rot:cat,cor:Dados.corLista(cat),
      go:()=>{ S.mural.push({id:Dados.novoId('m'),cat,t:txt,criado:new Date().toISOString()});
               tira(); renderListas(); toast('foi pra lista '+cat); }})));
    return;
  }
  if(destino==='agenda'){
    const ops=[0,1,2,7].map(d=>({rot:d===0?'hoje':d===1?'amanhã':'em '+d+' dias',cor:'var(--warn)',
      go:()=>{ S.tarefas.unshift(novaTarefa(txt,'a fazer',maisDias(d))); tira();
               renderAgenda(); renderEventos(); renderProximos(); toast('marcado na agenda'); }}));
    menu(alvoBtn,'PRA QUANDO',ops); return;
  }
  if(destino==='nota'){
    const n=novaNotaCom(txt);
    tira(); atualizaContadorNotas(); abrirSticky(n.id); toast('virou nota');
    return;
  }
}
/* menuzinho de destino ancorado no botão */
function menu(alvo,titulo,opcoes){
  document.querySelectorAll('.menu').forEach(m=>m.remove());
  const m=document.createElement('div'); m.className='menu';
  m.innerHTML=`<div class="mtit">${titulo}</div>`;
  opcoes.forEach(o=>{
    const b=document.createElement('button');
    b.innerHTML=`<i style="background:${o.cor}"></i><span>${esc(o.rot)}</span>`;
    b.onclick=()=>{ m.remove(); o.go(); };
    m.appendChild(b);
  });
  document.body.appendChild(m);
  const r=alvo.getBoundingClientRect();
  let top=r.bottom+6, left=r.left;
  if(top+m.offsetHeight>innerHeight-10) top=r.top-m.offsetHeight-6;
  if(left+m.offsetWidth>innerWidth-10) left=innerWidth-m.offsetWidth-10;
  m.style.top=top+'px'; m.style.left=left+'px';
  setTimeout(()=>document.addEventListener('mousedown',function fecha(e){
    if(!m.contains(e.target)){ m.remove(); document.removeEventListener('mousedown',fecha); }
  }),0);
}
function limparCaptura(){ S.captura=[]; salvar(); renderCaptura(); toast('captura limpa'); }
document.getElementById('capIn').onkeydown=e=>{
  if(e.key==='Enter'&&e.target.value.trim()){
    S.captura.unshift({id:Dados.novoId(),t:e.target.value.trim()}); e.target.value=''; salvar(); renderCaptura(); go('hoje'); toast('capturado ✓');
  }
};
/* Prazos pessoais e do trabalho na mesma lista, misturados por data —
   é assim que a semana chega de verdade. O que separa os dois é uma
   marca discreta, não uma lista à parte que você esqueceria de olhar. */
function renderProximos(){
  const box=document.getElementById('proximos'); box.innerHTML='';
  const hj=chave(new Date());

  const pessoais=S.tarefas.filter(t=>t.prazo&&t.prazo>=hj&&t.status!=='feito')
    .map(t=>({quando:t.prazo, texto:t.t, trabalho:false}));
  const doTrabalho=S.trabalho.filter(t=>t.deadline&&t.deadline>=hj&&t.status!=='done')
    .map(t=>({quando:t.deadline, texto:t.title||'sem título', trabalho:true, area:t.area}));

  const vindo=[...pessoais,...doTrabalho]
    .sort((a,b)=>a.quando.localeCompare(b.quando)).slice(0,8);

  if(!vindo.length){box.innerHTML='<div class="empty">agenda livre pela frente</div>';return;}
  vindo.forEach(ev=>{
    const d=new Date(ev.quando+'T12:00');
    const el=document.createElement('div'); el.className='ag';
    el.innerHTML=`<span class="d">${d.toLocaleDateString('pt-BR',{weekday:'short'}).slice(0,3).toUpperCase()} ${String(d.getDate()).padStart(2,'0')}</span>
      <span>${esc(ev.texto)}</span>` +
      (ev.trabalho?`<span class="tb" title="${esc(ev.area||'ConceWay')}">trabalho</span>`:'');
    el.style.cursor='pointer';
    el.onclick=()=>{
      if(ev.trabalho){ go('trabalho'); return; }
      diaSel=ev.quando; mesRef=new Date(d.getFullYear(),d.getMonth(),1);
      renderAgenda(); renderEventos(); go('agenda');
    };
    box.appendChild(el);
  });
}

/* ═══ KANBAN ═══
   As colunas são o campo `status` da tabela tarefas. Mover um card
   entre colunas é mudar o status; a ordem dentro da coluna vem da
   posição em S.tarefas, que é o que a chave zimbar_ordem guarda. */
const corCol=(ci)=>ci===0?'var(--acc)':ci===1?'var(--warn)':'var(--ok)';

function renderKanban(){
  const g=document.getElementById('kbGrid'); g.innerHTML='';
  COLUNAS.forEach((status,ci)=>{
    const itens=tarefasDa(status);
    const c=document.createElement('div'); c.className='card kcol'; c.dataset.col=ci;
    c.innerHTML=`<div class="ttl" style="padding-top:5px">
      <h2>${status.toUpperCase()}</h2><span class="sp"></span><span class="cnt">${itens.length}</span></div>
      <div class="scrollbox"></div>
      <div class="addline"><span class="plus">+</span><input placeholder="novo card…"></div>`;
    const box=c.querySelector('.scrollbox');
    zonaSoltar(c,'kb'+ci);   // a COLUNA inteira recebe, não só a lista de cards
    if(!itens.length) box.innerHTML='<div class="empty">arrasta um card pra cá</div>';
    itens.forEach((it,ii)=>{
      const k=document.createElement('div'); k.className='kcard';
      k.innerHTML=esc(it.t)+(it.prazo?`<small>${dLabel(it.prazo)}</small>`:'');
      k.title='arrasta pra mover · solta num módulo do trilho pra mandar pra lá · clica pra abrir';
      arrastavel(k,{zona:'kb'+ci,indice:ii,
        carga:{texto:it.t},          // o card FICA no kanban; vai uma cópia
        aoSoltar:(zona,idx)=>{
          if(!zona.startsWith('kb')) return;
          const dest=parseInt(zona.slice(2));
          it.status=COLUNAS[dest];
          recolocar(it, tarefasDa(COLUNAS[dest])[idx]);
          salvar(); renderKanban();
          if(dest!==ci) toast('foi pra '+COLUNAS[dest]);
        },
        aoAbrir:()=>abrirCard(it.id)});
      box.appendChild(k);
    });
    c.querySelector('input').onkeydown=e=>{
      if(e.key==='Enter'&&e.target.value.trim()){
        S.tarefas.unshift({id:Dados.novoId('t'),t:e.target.value.trim(),status,prazo:null,criado:new Date().toISOString()});
        e.target.value=''; salvar(); renderKanban();
      }
    };
    g.appendChild(c);
  });
  document.getElementById('ct-kanban').textContent=tarefasDa('a fazer').length||'';
}
/* tira a tarefa de onde está e põe logo antes de `antesDe` (ou no fim) */
function recolocar(item, antesDe){
  const i=S.tarefas.indexOf(item);
  if(i>=0) S.tarefas.splice(i,1);
  const j=antesDe&&antesDe!==item ? S.tarefas.indexOf(antesDe) : -1;
  if(j>=0) S.tarefas.splice(j,0,item); else S.tarefas.push(item);
}
function abrirCard(id){
  const it=S.tarefas.find(t=>t.id===id); if(!it) return;
  editor({titulo:'CARD DO KANBAN',campos:[
      {id:'t',rot:'TÍTULO',tipo:'linha',valor:it.t,dica:'o que precisa ser feito'},
      {id:'prazo',rot:'DIA (OPCIONAL)',tipo:'data',valor:it.prazo||'',dica:'aparece na agenda'},
      {id:'col',rot:'COLUNA',tipo:'opcoes',valor:it.status||'a fazer',
       opcoes:COLUNAS.map((n,i)=>({v:n,rot:n,cor:corCol(i)}))}
    ],
    acoes:[{rot:'→ plano de hoje',
            go:onde=>menu(onde,'PRO PLANO COMO',NIVEIS.map(n=>({rot:n.un,cor:n.cor,
              go:()=>{ S.hoje.push({id:Dados.novoId(),t:it.t,n:n.k,f:false});
                       salvar(); renderHoje(); toast('foi pro plano de hoje'); }})))}],
    aoSalvar:v=>{
      it.t=v.t.trim()||it.t;
      it.prazo=v.prazo?v.prazo:null;
      it.status=v.col;
      salvar(); renderTudo(); toast('card salvo');
    },
    aoApagar:()=>{ S.tarefas=S.tarefas.filter(t=>t.id!==id); salvar(); renderTudo(); toast('card apagado'); }});
}

/* ═══ AGENDA ═══ */
let mesRef=new Date(new Date().getFullYear(),new Date().getMonth(),1), diaSel=chave(new Date());
function renderAgenda(){
  const g=document.getElementById('calGrid'); g.innerHTML='';
  const y=mesRef.getFullYear(), m=mesRef.getMonth();
  document.getElementById('mesTitulo').textContent=
    mesRef.toLocaleDateString('pt-BR',{month:'long',year:'numeric'}).toUpperCase();
  const prim=new Date(y,m,1), off=(prim.getDay()+6)%7, dias=new Date(y,m+1,0).getDate();
  for(let i=0;i<off;i++){const d=document.createElement('div');d.className='cd out';g.appendChild(d);}
  const hojeStr=chave(new Date());
  for(let d=1;d<=dias;d++){
    const key=`${y}-${String(m+1).padStart(2,'0')}-${String(d).padStart(2,'0')}`;
    const el=document.createElement('div');
    el.className='cd'+(key===hojeStr?' hoje':'');
    if(key===diaSel) el.style.borderColor='var(--acc)';
    el.innerHTML='<b>'+d+'</b>';
    zonaSoltar(el,'dia:'+key);       // soltar aqui muda o prazo da tarefa
    tarefasDoDia(key).slice(0,2).forEach((ev,ei)=>{
      const ch=document.createElement('span'); ch.className='ev'; ch.textContent=ev.t;
      arrastavel(ch,{zona:'dia:'+key,indice:ei,
        carga:{texto:ev.t},
        aoSoltar:(zona)=>{
          if(!zona.startsWith('dia:'))return;
          const novo=zona.slice(4); if(novo===key)return;
          ev.prazo=novo;
          salvar(); renderAgenda(); renderEventos(); renderProximos(); toast('remarcado');
        },
        aoAbrir:()=>{ diaSel=key; renderAgenda(); renderEventos(); }});
      el.appendChild(ch);
    });
    g.appendChild(el);
    el.onclick=e=>{ if(e.target===el||e.target.tagName==='B'){ diaSel=key; renderAgenda(); renderEventos(); } };
  }
  document.getElementById('ct-agenda').textContent=S.tarefas.filter(t=>t.prazo).length||'';
}
function renderEventos(){
  const box=document.getElementById('listaEv'); box.innerHTML='';
  const d=new Date(diaSel+'T12:00');
  const cab=document.createElement('div');
  cab.style.cssText='font-size:13px;font-weight:600;margin-bottom:8px';
  cab.textContent=d.toLocaleDateString('pt-BR',{weekday:'long',day:'2-digit',month:'long'});
  box.appendChild(cab);
  const evs=tarefasDoDia(diaSel);
  if(!evs.length){const e=document.createElement('div');e.className='empty';e.textContent='nada nesse dia';box.appendChild(e);}
  const lista=document.createElement('div'); zonaSoltar(lista,'evdia'); box.appendChild(lista);
  evs.forEach((ev,i)=>{
    const el=document.createElement('div'); el.className='tk'+(ev.status==='feito'?' done':'');
    el.innerHTML=`<div class="tx">${esc(ev.t)}</div><span class="qt">${ev.status}</span><button class="del">✕</button>`;
    el.querySelector('.del').onclick=e=>{ e.stopPropagation();
      S.tarefas=S.tarefas.filter(t=>t.id!==ev.id);
      salvar(); renderTudo(); };
    el.title='arrasta pro calendário pra mudar o dia · solta num módulo do trilho pra mandar pra lá · clica pra editar';
    arrastavel(el,{zona:'evdia',indice:i,
      carga:{texto:ev.t},
      aoSoltar:(zona,idx)=>{
        if(zona==='evdia'){ recolocar(ev, tarefasDoDia(diaSel)[idx]); salvar(); renderEventos(); return; }
        if(zona.startsWith('dia:')){
          const novo=zona.slice(4); if(novo===diaSel)return;
          ev.prazo=novo;
          salvar(); renderAgenda(); renderEventos(); renderProximos(); toast('remarcado');
        }
      },
      aoAbrir:()=>abrirCard(ev.id)});
    lista.appendChild(el);
  });
}
document.getElementById('addEv').onkeydown=e=>{
  if(e.key==='Enter'&&e.target.value.trim()){
    S.tarefas.unshift({id:Dados.novoId('t'),t:e.target.value.trim(),status:'a fazer',
                       prazo:diaSel,criado:new Date().toISOString()});
    e.target.value=''; salvar(); renderTudo();
  }
};
function mudaMes(n){ mesRef=new Date(mesRef.getFullYear(),mesRef.getMonth()+n,1); renderAgenda(); }

/* ═══ LISTAS ═══
   Cada pasta é uma `categoria` da tabela mural_items. Mover um item
   entre pastas é trocar a categoria dele. */
function renderListas(){
  const g=document.getElementById('listasGrid'); g.innerHTML='';
  const cats=categorias();
  if(!cats.length) g.innerHTML='<div class="empty">nenhuma lista ainda — cria uma abaixo</div>';
  cats.forEach((cat,fi)=>{
    const itens=listaDe(cat);
    const c=document.createElement('div'); c.className='fold';
    c.innerHTML=`<h3><i style="background:${Dados.corLista(cat)}"></i>${esc(cat)}</h3>`;
    const corpo=document.createElement('div'); corpo.className='corpo';
    zonaSoltar(c,'lst'+fi);   // a pasta inteira recebe, inclusive o título
    if(!itens.length) corpo.innerHTML='<div class="empty" style="padding:4px 0">arrasta um item pra cá</div>';
    itens.forEach((it,ii)=>{
      const r=document.createElement('div'); r.className='lk';
      r.innerHTML=`<i></i><span>${esc(it.t)}</span><button class="del">✕</button>`;
      r.querySelector('.del').onclick=ev=>{ ev.stopPropagation();
        S.mural=S.mural.filter(m=>m.id!==it.id); salvar(); renderListas(); };
      r.title='arrasta pra mover · solta num módulo do trilho pra mandar pra lá · clica pra editar';
      arrastavel(r,{zona:'lst'+fi,indice:ii,
        carga:{texto:it.t},
        aoSoltar:(zona,idx)=>{
          if(!zona.startsWith('lst')) return;
          const destino=cats[parseInt(zona.slice(3))];
          it.cat=destino;
          const vizinho=listaDe(destino)[idx];
          const i=S.mural.indexOf(it); if(i>=0) S.mural.splice(i,1);
          const j=vizinho&&vizinho!==it?S.mural.indexOf(vizinho):-1;
          if(j>=0) S.mural.splice(j,0,it); else S.mural.push(it);
          salvar(); renderListas();
        },
        aoAbrir:()=>abrirItemLista(it.id)});
      corpo.appendChild(r);
    });
    c.appendChild(corpo);
    const add=document.createElement('div'); add.className='addline';
    add.innerHTML='<span class="plus">+</span><input placeholder="novo item…">';
    add.querySelector('input').onkeydown=e=>{
      if(e.key==='Enter'&&e.target.value.trim()){
        S.mural.push({id:Dados.novoId('m'),cat,t:e.target.value.trim(),criado:new Date().toISOString()});
        e.target.value=''; salvar(); renderListas();
      }
    };
    c.appendChild(add);
    g.appendChild(c);
  });
  document.getElementById('ct-listas').textContent=S.mural.length||'';
}
function abrirItemLista(id){
  const it=S.mural.find(m=>m.id===id); if(!it) return;
  const cats=categorias();
  editor({titulo:'ITEM DA LISTA',campos:[
      {id:'t',rot:'ITEM',tipo:'linha',valor:it.t},
      {id:'l',rot:'EM QUAL LISTA',tipo:'opcoes',valor:it.cat,
       opcoes:cats.map(c=>({v:c,rot:c,cor:Dados.corLista(c)}))}
    ],
    aoSalvar:v=>{
      const novo=v.t.trim(); if(!novo) return;
      it.t=novo; it.cat=v.l;
      salvar(); renderListas(); toast('item salvo');
    },
    aoApagar:()=>{ S.mural=S.mural.filter(m=>m.id!==id); salvar(); renderListas(); toast('item apagado'); }});
}
/* criar uma pasta = primeiro item de uma categoria nova */
function novaLista(nome){
  const n=nome.trim().toLowerCase(); if(!n) return;
  if(categorias().includes(n)){ toast('essa lista já existe'); return; }
  S.ordem.listas=[...categorias(), n];
  salvar(); renderListas(); toast('lista "'+n+'" criada');
}

/* ═══ NOTAS — ferramenta, em janelas soltas (autoadesivas) ═══ */
let zTopo=60, cascata=0;

function atualizaContadorNotas(){
  const el=document.getElementById('ct-notas');
  if(el) el.textContent=S.notas.length||'';
}
/* arrastar + redimensionar qualquer janela */
function tornarJanela(j,handle){
  j.style.zIndex=++zTopo;
  j.addEventListener('mousedown',()=>{ j.style.zIndex=++zTopo; });
  handle.addEventListener('mousedown',e=>{
    if(e.target.tagName==='BUTTON')return;
    const r=j.getBoundingClientRect(), dx=e.clientX-r.left, dy=e.clientY-r.top;
    const mv=ev=>{ j.style.left=Math.max(4,Math.min(innerWidth-60,ev.clientX-dx))+'px';
                   j.style.top =Math.max(4,Math.min(innerHeight-40,ev.clientY-dy))+'px'; };
    const up=()=>{ document.removeEventListener('mousemove',mv); document.removeEventListener('mouseup',up); };
    document.addEventListener('mousemove',mv); document.addEventListener('mouseup',up);
    e.preventDefault();
  });
  const rz=j.querySelector('.redim');
  if(rz) rz.addEventListener('mousedown',e=>{
    const r=j.getBoundingClientRect(), x0=e.clientX, y0=e.clientY, w0=r.width, h0=r.height;
    const mv=ev=>{ j.style.width=Math.max(200,w0+ev.clientX-x0)+'px';
                   j.style.height=Math.max(160,h0+ev.clientY-y0)+'px'; };
    const up=()=>{ document.removeEventListener('mousemove',mv); document.removeEventListener('mouseup',up); };
    document.addEventListener('mousemove',mv); document.addEventListener('mouseup',up);
    e.preventDefault(); e.stopPropagation();
  });
}
/* biblioteca encosta na direita; as adesivas cascateiam à esquerda dela */
function posBiblioteca(){ return {l:innerWidth-338, t:104}; }

/* ── biblioteca de notas ── */
function abrirBiblioteca(){
  const jaAberta=document.getElementById('janBib');
  if(jaAberta){ jaAberta.style.zIndex=++zTopo; return; }
  const j=document.createElement('div'); j.className='jan bib'; j.id='janBib';
  const p=posBiblioteca(); j.style.left=p.l+'px'; j.style.top=p.t+'px';
  j.innerHTML=`<div class="barra"><span class="tt">✎ Minhas notas</span>
      <button title="nova nota" id="bibNova">＋</button><button title="fechar" id="bibX">✕</button></div>
    <div class="lista" id="bibLista"></div><div class="redim"></div>`;
  document.body.appendChild(j);
  tornarJanela(j,j.querySelector('.barra'));
  j.querySelector('#bibX').onclick=()=>j.remove();
  j.querySelector('#bibNova').onclick=()=>novaNota();
  renderBiblioteca();
}
function renderBiblioteca(){
  const box=document.getElementById('bibLista'); if(!box) return;
  box.innerHTML='';
  zonaSoltar(box,'notas');
  if(!S.notas.length){ box.innerHTML='<div class="empty">nenhuma nota — usa o ＋ ali em cima</div>'; return; }
  S.notas.forEach((n,ni)=>{
    const el=document.createElement('div'); el.className='nt';
    const previa=(n.b||'').split('\n').filter(l=>l.trim())[0]||'vazia';
    el.innerHTML=`<div class="fita" style="background:${Dados.corHex(n.c)}"></div>
      <div class="ni"><div class="nt1">${esc(n.t||'sem título')}</div><div class="nt2">${esc(previa)}</div></div>`;
    el.title='arrasta pra reordenar · solta num módulo do trilho pra mandar pra lá · clica pra abrir';
    arrastavel(el,{zona:'notas',indice:ni,
      carga:{texto:n.t||previa},
      aoSoltar:(zona,idx)=>{ if(zona!=='notas')return;
        const [m]=S.notas.splice(ni,1); S.notas.splice(idx,0,m); salvar(); renderBiblioteca(); },
      aoAbrir:()=>abrirSticky(n.id)});
    box.appendChild(el);
  });
}
function novaNota(){
  const n={id:Dados.novoId(),t:'',
           c:Dados.CORES_NOTA[S.notas.length%Dados.CORES_NOTA.length].k,b:''};
  S.notas.unshift(n); salvar(); renderBiblioteca(); atualizaContadorNotas();
  // grava antes de abrir: a janela nativa edita direto no banco
  sincronizar().then(()=>abrirSticky(n.id,true));
}
/* ── autoadesiva ──
   A nota abre como JANELA DE VERDADE do Windows (a mesma do ZimNotes),
   não como caixinha dentro do app: some da barra, fica por cima de tudo,
   salva sozinha enquanto você digita. Quem faz isso é o C#. */
function abrirSticky(id,focar){
  const n=S.notas.find(x=>x.id===id); if(!n) return;
  if(!Ponte.tem()){ toast('as notas soltas só abrem no aplicativo'); return; }
  Ponte.enviar({acao:'abrirNota', id:n.id, titulo:n.t||'', corpo:n.b||'', cor:n.c||''});
}

/* ═══════════════════════════════════════════════════════════════
   NOTÍCIAS — igual ao Zimbar de sempre: seções fixas em cima e uma
   grade de manchetes com imagem. As capas aqui são geradas na hora
   (o app real usa a foto que vem do feed).
   ═══════════════════════════════════════════════════════════════ */
const SECOES=[
  {k:'destaques',      rot:'destaques'},
  {k:'mundo',          rot:'mundo'},
  {k:'tecnologia',     rot:'tecnologia'},
  {k:'negocios',       rot:'negócios'},
  {k:'esportes',       rot:'esportes'},
  {k:'entretenimento', rot:'entretenimento'}
];
/* As manchetes vêm de verdade, do RSS de notícias em pt-BR. Quem busca
   é o C#: o feed não libera CORS, então o navegador aqui dentro não
   conseguiria pedir sozinho. Guarda 10 min por seção, igual antes. */
const FEED={};        // seção -> manchetes já buscadas
const BUSCANDO={};    // seção -> promessa em andamento

async function buscarSecao(secao, forcar){
  if(!forcar && FEED[secao]) return FEED[secao];
  if(BUSCANDO[secao]) return BUSCANDO[secao];
  BUSCANDO[secao] = Ponte.pedir({acao:'noticias', secao, forcar:!!forcar})
    .then(r=>{ FEED[secao]=r&&r.itens||[]; delete BUSCANDO[secao]; return FEED[secao]; })
    .catch(e=>{ delete BUSCANDO[secao]; throw e; });
  return BUSCANDO[secao];
}
/* capa gerada a partir do título: cada manchete tem a sua, sempre a mesma */
function digitos(s){ let h=2166136261;
  for(let i=0;i<s.length;i++){ h^=s.charCodeAt(i); h=Math.imul(h,16777619); }
  return h>>>0; }
function capa(seed){
  const h=digitos(seed);
  const t1=h%360, t2=(t1+38+(h>>3)%86)%360;
  const sat=42+(h>>5)%20, luz=44+(h>>7)%12;
  const cx=18+(h>>9)%76, cy=12+(h>>11)%56, r=20+(h>>13)%28;
  const rx=(h>>15)%84, ry=(h>>17)%46, rot=(h>>19)%64-32;
  const rw=44+(h>>21)%42, rh=15+(h>>23)%24;
  const svg=
   '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 160 90" preserveAspectRatio="xMidYMid slice">'
  +'<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">'
  +'<stop offset="0" stop-color="hsl('+t1+','+sat+'%,'+luz+'%)"/>'
  +'<stop offset="1" stop-color="hsl('+t2+','+(sat+10)+'%,'+(luz-17)+'%)"/></linearGradient></defs>'
  +'<rect width="160" height="90" fill="url(#g)"/>'
  +'<circle cx="'+cx+'" cy="'+cy+'" r="'+r+'" fill="#ffffff" opacity="0.13"/>'
  +'<rect x="'+rx+'" y="'+ry+'" width="'+rw+'" height="'+rh+'" rx="3" fill="#000000" opacity="0.13" '
  +'transform="rotate('+rot+' '+(rx+rw/2)+' '+(ry+rh/2)+')"/>'
  +'<path d="M0 '+(60+(h>>25)%18)+' Q40 '+(38+(h>>3)%30)+' 80 '+(56+(h>>7)%24)+' T160 '+(48+(h>>11)%28)+'" '
  +'stroke="#ffffff" stroke-opacity="0.22" stroke-width="1.6" fill="none"/>'
  +'<rect y="56" width="160" height="34" fill="#000000" opacity="0.2"/>'
  +'</svg>';
  return 'data:image/svg+xml;charset=utf-8,'+encodeURIComponent(svg);
}

function listaSecoes(){
  return [...SECOES, ...S.topicos
    .filter(t=>!SECOES.some(s=>s.k===t))
    .map(t=>({k:t,rot:t,minha:true}))];
}
function renderTopicos(){
  const box=document.getElementById('nwSecs'); if(!box) return;
  box.innerHTML='';
  const todas=listaSecoes();
  if(!todas.some(s=>s.k===S.secaoAtiva)) S.secaoAtiva=todas[0].k;
  todas.forEach(s=>{
    const el=document.createElement('button');
    el.className='sec'+(s.k===S.secaoAtiva?' on':'');
    el.innerHTML='<i></i><span>'+esc(s.rot)+'</span>'+(s.minha?'<button class="del">✕</button>':'');
    el.onclick=()=>{ S.secaoAtiva=s.k; salvar(); renderTopicos(); renderFeed(); };
    el.querySelector('.del')?.addEventListener('click',e=>{ e.stopPropagation();
      S.topicos=S.topicos.filter(t=>t!==s.k);
      if(S.secaoAtiva===s.k) S.secaoAtiva='destaques';
      salvar(); renderTopicos(); renderFeed(); });
    box.appendChild(el);
  });
}
async function renderFeed(forcar){
  const box=document.getElementById('nwFeed'); if(!box) return;
  const atual=listaSecoes().find(s=>s.k===S.secaoAtiva)||listaSecoes()[0];
  document.getElementById('nwTitulo').textContent=atual.rot.toUpperCase();

  let itens=FEED[atual.k];
  if(!itens||forcar){
    box.innerHTML='<div class="empty">buscando manchetes…</div>';
    document.getElementById('nwQtd').textContent='';
    try{ itens=await buscarSecao(atual.k,forcar); }
    catch(e){
      box.innerHTML='<div class="empty">sem internet agora — tenta atualizar daqui a pouco</div>';
      return;
    }
    if(S.secaoAtiva!==atual.k) return;    // trocou de seção enquanto buscava
  }
  box.innerHTML='';
  if(!itens.length){ box.innerHTML='<div class="empty">nada por agora nessa seção</div>'; return; }
  document.getElementById('nwQtd').textContent=itens.length+' manchetes';
  document.getElementById('ct-noticias').textContent=itens.length;

  const g=document.createElement('div'); g.className='grid-nw';
  zonaSoltar(g,'nw');
  itens.forEach(n=>{
    const el=document.createElement('div'); el.className='nwc';
    el.innerHTML='<div class="im"><img alt="" loading="lazy" src="'+(n.img||capa(n.t))+
        '" onerror="this.src=capa(this.closest(\'.nwc\').querySelector(\'.tt\').textContent)"><b>'+esc(n.f||'')+'</b></div>'
      +'<div class="bd"><div class="tt">'+esc(n.t)+'</div>'
      +'<div class="mt">'+esc(n.h||'')+'</div></div>';
    el.title='arrasta pra um módulo do trilho pra guardar · clica pra abrir a matéria';
    arrastavel(el,{zona:'nw',indice:0,
      carga:{texto:n.t},
      aoAbrir:()=>{ if(n.link) Ponte.enviar({acao:'abrirLink',url:n.link});
                    else toast('essa manchete não tem link'); }});
    g.appendChild(el);
  });
  box.appendChild(g);
}
document.getElementById('addTopico').onkeydown=e=>{
  if(e.key==='Enter'&&e.target.value.trim()){
    const t=e.target.value.trim().toLowerCase();
    if(!S.topicos.includes(t)&&!SECOES.some(s=>s.k===t)) S.topicos.push(t);
    S.secaoAtiva=t; e.target.value=''; salvar(); renderTopicos(); renderFeed();
    toast('seção "'+t+'" criada');
  }
};

/* ═══════════════════════════════════════════════════════════════
   CONTAS — mesma conta que o contaspedro faz, agora aqui:
     livre = saldo + salários até a meta − fatura − parcelas/fixos − objetivo
     pode gastar hoje = (livre − gastos do período) / dias que faltam
   As parcelas vêm da tabela `compras` (nome, d, parcelas, vp) e os
   `fixos` (com override por mês). Nada de número chumbado.
   ═══════════════════════════════════════════════════════════════ */
const mesesAbertos=new Set();
const MESES_PT=['jan','fev','mar','abr','mai','jun','jul','ago','set','out','nov','dez'];
const num=v=>{ const n=parseFloat(String(v??'').replace(',','.')); return isFinite(n)?n:0; };
const fmt=v=>'R$ '+v.toLocaleString('pt-BR',{minimumFractionDigits:2,maximumFractionDigits:2});
/* dias inteiros entre duas datas ISO, sem o fuso atrapalhar */
const dias=(a,b)=>Math.round((new Date(b+'T12:00')-new Date(a+'T12:00'))/864e5);
/* o DIA local de um carimbo de tempo do banco */
function diaLocal(iso){
  const d=iso?new Date(iso):new Date();
  return d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0');
}

/* parcelas e fixos que caem num ano/mês (mo = 1-12) */
function itensDoMes(compras,fixos,y,mo){
  const itens=[];
  for(const c of compras){
    const p=String(c.d||'').split('-');
    if(p.length<3) continue;
    const cy=parseInt(p[0]), cm=parseInt(p[1]);
    const parcelas=Math.round(num(c.parcelas)), vp=num(c.vp);
    for(let i=0;i<parcelas;i++){
      let nm=cm-1+i;
      const ny=cy+Math.floor(nm/12);
      nm=((nm%12)+12)%12;
      if(ny===y&&nm===mo-1)
        itens.push({n:parcelas>1?`${c.nome||'compra'} (${i+1}/${parcelas})`:(c.nome||'compra'), v:vp});
    }
  }
  const fk=`${y}-${String(mo).padStart(2,'0')}`;
  for(const f of fixos){
    const v=(f.overrides&&f.overrides[fk]!==undefined)?num(f.overrides[fk]):num(f.valor);
    if(v!==0) itens.push({n:f.nome||'fixo', v});
  }
  return itens.sort((a,b)=>b.v-a.v);
}
const totalDoMes=(compras,fixos,y,mo)=>itensDoMes(compras,fixos,y,mo).reduce((s,i)=>s+i.v,0);

function renderContas(){
  const c=S.conta;
  if(!c||!c.meta){
    document.getElementById('ctHv').textContent='—';
    document.getElementById('ctHs').textContent='sem meta cadastrada no contas';
    return;
  }
  // ── mesma conta do contaspedro, modelo ACUMULADOR ──
  // O bolo (saldo + salários que ainda caem − fatura − parcelas/fixos − meta)
  // vira uma cota diária fixa. O que você não gasta num dia fica guardado
  // pro dia seguinte; por isso "pode gastar" cresce quando você segura.
  const m=c.meta;
  const hojeIso=Dados.hojeChave();
  const alvoIso=m.data_alvo||hojeIso;
  const refIso=diaLocal(m.atualizado_em||m.criado_em);
  const objetivo=num(m.objetivo), saldo=num(m.saldo_conta), fatura=num(m.fatura);
  const salario=num(m.salario), diaPag=Math.round(num(m.dia_pagamento));

  // salários entre a última atualização de saldo e a meta
  let salTotal=0;
  if(salario>0&&diaPag>0){
    let [y,mo]=refIso.split('-').map(Number);
    const [ay,am]=alvoIso.split('-').map(Number);
    while(y<ay||(y===ay&&mo<=am)){
      const dim=new Date(y,mo,0).getDate();
      const k=`${y}-${String(mo).padStart(2,'0')}-${String(Math.min(diaPag,dim)).padStart(2,'0')}`;
      if(k>refIso&&k<=alvoIso) salTotal+=salario;
      mo++; if(mo>12){mo=1;y++;}
    }
  }
  // parcelas e fixos dos meses SEGUINTES ao atual, até a meta
  let comprometido=0;
  {
    let [y,mo]=hojeIso.split('-').map(Number);
    const [ay,am]=alvoIso.split('-').map(Number);
    mo++; if(mo>12){mo=1;y++;}
    while(y<ay||(y===ay&&mo<=am)){
      comprometido+=totalDoMes(c.compras,c.fixos,y,mo);
      mo++; if(mo>12){mo=1;y++;}
    }
  }
  const periodo=c.gastos.filter(g=>String(g.criado_em||'')>String(m.atualizado_em||''));
  const gastoDesde=periodo.reduce((s,g)=>s+num(g.valor),0);

  const bolo=saldo+salTotal-fatura-comprometido-objetivo;
  const diasTotais=Math.max(1,dias(refIso,alvoIso)+1);
  const cotaDia=bolo/diasTotais;
  const diasCorridos=Math.min(diasTotais,Math.max(1,dias(refIso,hojeIso)+1));
  const disponivel=cotaDia*diasCorridos-gastoDesde;   // pode gastar agora
  const faltam=dias(hojeIso,alvoIso)+1;

  document.getElementById('mvHoje').textContent=fmt(disponivel);
  document.getElementById('ctHv').textContent=fmt(disponivel);
  document.getElementById('ctHs').textContent=
    faltam+' dias até a meta · '+alvoIso.split('-').reverse().join('/')+
    '  ·  cota de '+fmt(cotaDia)+'/dia';
  document.getElementById('mrDias').textContent=faltam+' dias até a meta';
  document.getElementById('mrLivre').textContent='cota '+fmt(cotaDia)+'/dia';
  document.getElementById('barMeta').style.width=
    Math.min(100,Math.max(3,(diasCorridos/diasTotais)*100))+'%';
  document.querySelector('[data-v=contas] .ct').textContent=fmt(disponivel);
  if(!document.getElementById('fSaldo').matches(':focus'))
    document.getElementById('fSaldo').value=saldo.toFixed(2).replace('.',',');
  if(!document.getElementById('fFatura').matches(':focus'))
    document.getElementById('fFatura').value=fatura.toFixed(2).replace('.',',');

  // próximos 6 meses da planilha
  const box=document.getElementById('mesesBox'); box.innerHTML='';
  const agora=new Date();
  const linhas=[];
  for(let i=0;i<6;i++){
    const d=new Date(agora.getFullYear(),agora.getMonth()+i,1);
    const its=itensDoMes(c.compras,c.fixos,d.getFullYear(),d.getMonth()+1);
    linhas.push({d,tot:its.reduce((s,x)=>s+x.v,0),its});
  }
  const max=Math.max(...linhas.map(l=>l.tot),1);
  linhas.forEach((l,li)=>{
    const el=document.createElement('div'); el.className='mrow';
    const rot=MESES_PT[l.d.getMonth()]+'/'+String(l.d.getFullYear()).slice(2);
    const aberto=mesesAbertos.has(rot);
    const mostra=aberto?l.its:l.its.slice(0,3);
    el.innerHTML=`<div class="mh"><span class="mn">${rot}${li===0?' <span style="font-size:10px;color:var(--faint)">· mês atual</span>':''}<span class="lupa">${aberto?'▾ fechar':'▸ ver tudo'}</span></span><span>${fmt(l.tot)}</span></div>
      <div class="mbar"><i style="width:${(l.tot/max)*100}%"></i></div>
      ${mostra.map(i=>`<div class="mit"><span>${esc(i.n)}</span><span>${fmt(i.v)}</span></div>`).join('')}
      ${(!aberto&&l.its.length>3)?`<div class="mit" style="opacity:.6"><span>+${l.its.length-3} outros</span><span>${fmt(l.its.slice(3).reduce((s,i)=>s+i.v,0))}</span></div>`:''}`;
    el.title=aberto?'clica pra fechar':`clica pra ver os ${l.its.length} lançamentos do mês`;
    el.onclick=()=>{ aberto?mesesAbertos.delete(rot):mesesAbertos.add(rot); renderContas(); };
    box.appendChild(el);
  });
}
/* grava saldo+fatura na meta; isso reinicia o período de gastos, igual no site */
async function atualizarConta(){
  const saldo=num(document.getElementById('fSaldo').value);
  const fatura=num(document.getElementById('fFatura').value);
  try{
    await Dados.atualizarConta(saldo,fatura);
    await carregarContas();
    toast('conta atualizada');
  }catch(e){ console.error(e); toast('não deu pra atualizar'); }
}
/* Busca e desenho são separados de propósito: assim um erro de desenho
   nunca mais se disfarça de "sem conexão" e some com o painel inteiro. */
async function carregarContas(){
  try{ S.conta=await Dados.contas(); }
  catch(e){
    console.error('contas — busca',e);
    document.getElementById('ctHs').textContent='sem conexão com o contas agora';
    return;
  }
  try{ renderContas(); }
  catch(e){
    console.error('contas — desenho',e);
    document.getElementById('ctHs').textContent='erro ao montar o painel: '+e.message;
  }
}

/* ═══ POMODORO ═══
   Mora numa janelinha separada: continua contando com o Zimbar fechado. */
document.getElementById('pomo').onclick=()=>{
  if(!Ponte.tem()){ toast('o pomodoro só abre no aplicativo'); return; }
  Ponte.enviar({acao:'pomodoro'});
};

/* ═══ UTIL ═══ */
function esc(s){return String(s).replace(/[<>&]/g,c=>({'<':'&lt;','>':'&gt;','&':'&amp;'}[c]));}
let tT; function toast(m){const t=document.getElementById('toast');t.textContent=m;t.classList.add('show');
  clearTimeout(tT);tT=setTimeout(()=>t.classList.remove('show'),1700);}

/* ═══ BOOT ═══ */
/* o trilho inteiro recebe itens: arrasta qualquer coisa pra cima de um
   módulo e ele pergunta onde encaixar. É o atalho entre as abas. */
document.querySelectorAll('.nv').forEach(b=>zonaSoltar(b,'rail:'+b.dataset.v));
zonaSoltar(document.getElementById('toolNotas'),'rail:notas');

aplicaTema();
renderTopicos();

async function recarregar(){
  sinal('carregando');
  try{
    const d=await Dados.carregar();
    S.hoje=d.hoje; S.frog=d.frog; S.captura=d.captura; S.ritmo=d.ritmo;
    S.tarefas=d.tarefas; S.mural=d.mural; S.notas=d.notas; S.ordem=d.ordem;
    BASE=retrato();
    renderTudo(); renderRitmo();
    sinal('');
  }catch(e){
    console.error('carregar',e);
    sinal('sem conexão', true);
    document.getElementById('listaHoje').innerHTML=
      '<div class="empty">não deu pra falar com o banco — confere a internet e tenta de novo</div>';
  }
  carregarContas();
  carregarTrabalho();
  renderFeed();
}

/* ═══ TRABALHO (ConceWay) ═══
   Banco é o mesmo, tabela é outra e é compartilhada com a equipe.
   Falhar aqui não pode derrubar o resto do Zimbar, então vive no
   próprio try. */
async function carregarTrabalho(){
  try{
    S.trabalho = await Dados.trabalho();
    renderTrabalho(); renderProximos();
  }catch(e){
    console.error('trabalho',e);
    const g=document.getElementById('tbGrid');
    if(g) g.innerHTML='<div class="empty">não deu pra falar com o ConceWay agora</div>';
  }
}

const trabalhoDe = (status) => S.trabalho.filter(t=>(t.status||'todo')===status);

function renderTrabalho(){
  const g=document.getElementById('tbGrid'); if(!g) return;
  g.innerHTML='';
  Dados.COLUNAS_TRABALHO.forEach((col,ci)=>{
    const itens=trabalhoDe(col.k);
    const c=document.createElement('div'); c.className='card kcol'; c.dataset.col=ci;
    c.innerHTML=`<div class="ttl" style="padding-top:5px">
      <h2>${col.rot}</h2><span class="sp"></span><span class="cnt">${itens.length}</span></div>
      <div class="scrollbox"></div>`;
    const box=c.querySelector('.scrollbox');
    zonaSoltar(c,'tb'+ci);
    if(!itens.length) box.innerHTML='<div class="empty">nada aqui</div>';
    itens.forEach((it,ii)=>{
      const k=document.createElement('div'); k.className='kcard';
      const atrasada = it.deadline && it.deadline < chave(new Date()) && col.k!=='done';
      k.innerHTML=esc(it.title||'sem título')+
        `<small>${it.area?esc(it.area):''}${it.area&&it.deadline?' · ':''}` +
        `${it.deadline?(atrasada?'⚠ ':'')+dLabel(it.deadline):''}</small>`;
      if(atrasada) k.style.borderColor='var(--acc)';
      k.title='arrasta pra mover — muda aqui e no ConceWay';
      arrastavel(k,{zona:'tb'+ci,indice:ii,
        carga:{texto:it.title||''},   // o card fica; pro resto do Zimbar vai uma cópia
        aoSoltar:(zona)=>{
          if(!zona.startsWith('tb')) return;
          const dest=parseInt(zona.slice(2));
          if(dest===ci) return;
          moverTrabalho(it, Dados.COLUNAS_TRABALHO[dest]);
        },
        aoAbrir:()=>abrirCardTrabalho(it)});
      box.appendChild(k);
    });
    g.appendChild(c);
  });
  const abertas=trabalhoDe('todo').length+trabalhoDe('doing').length;
  const ct=document.getElementById('ct-trabalho');
  if(ct) ct.textContent=abertas||'';
}

/* grava na hora: o ConceWay é de outra gente também, não dá pra
   deixar a mudança esperando um debounce que pode nem chegar */
async function moverTrabalho(it, col){
  const antes=it.status;
  it.status=col.k;
  renderTrabalho();
  sinal('salvando');
  try{
    await Dados.moverTask(it.id, col.k);
    sinal('');
    toast('foi pra '+col.rot.toLowerCase()+' — no ConceWay também');
    renderProximos();
  }catch(e){
    console.error('moverTask',e);
    it.status=antes; renderTrabalho();       // desfaz: o banco é a verdade
    sinal('não salvou', true);
    toast('não deu pra mover no ConceWay');
  }
}

/* Só leitura de propósito: o texto da tarefa é da equipe inteira, então
   editar é lá. Aqui você move de coluna, que é o que muda o seu dia. */
function abrirCardTrabalho(it){
  const quando = it.deadline ? dLabel(it.deadline) : 'sem prazo';
  editor({titulo:'TAREFA DO CONCEWAY',
    campos:[
      {id:'t', rot:'TAREFA', tipo:'linha', valor:it.title||''},
      {id:'d', rot:'DETALHE', tipo:'texto', valor:it.description||'—'},
      {id:'a', rot:'ÁREA · PRAZO', tipo:'linha', valor:(it.area||'—')+'  ·  '+quando}
    ],
    acoes:[{rot:'abrir no ConceWay',
            go:()=>Ponte.enviar({acao:'abrirLink', url:'https://conceway-app.netlify.app/'})}],
    aoSalvar:()=>toast('o texto se edita no ConceWay — aqui você move de coluna')});
}
recarregar();

/* ao voltar pra janela depois de um tempo, pega o que mudou em outro lugar */
let saiuEm=0;
document.addEventListener('visibilitychange',()=>{
  if(document.hidden){ saiuEm=Date.now(); return; }
  if(saiuEm && Date.now()-saiuEm > 120000) recarregar();
});

/* criar lista nova (uma categoria nova do mural) */
document.getElementById('addLista').onkeydown=e=>{
  if(e.key==='Enter'&&e.target.value.trim()){ novaLista(e.target.value); e.target.value=''; }
};

/* ferramentas do rodapé do trilho */
document.getElementById('toolRecarregar').onclick=()=>recarregar();

/* ═══ NOVO EVENTO — escolhe o dia num calendário, sem digitar data ═══ */
function novoEvento(){
  editor({titulo:'NOVO EVENTO',campos:[
      {id:'t',rot:'O QUE É',tipo:'linha',valor:'',dica:'ex: 18h gravação com o Breno'},
      {id:'d',rot:'DIA',tipo:'data',valor:diaSel},
      {id:'col',rot:'COLUNA NO KANBAN',tipo:'opcoes',valor:'a fazer',
       opcoes:COLUNAS.map((n,i)=>({v:n,rot:n,cor:corCol(i)}))}
    ],
    aoSalvar:v=>{
      const texto=v.t.trim(); if(!texto){ toast('escreve o que é'); return; }
      S.tarefas.unshift(novaTarefa(texto,v.col,v.d||diaSel));
      if(v.d) diaSel=v.d;
      salvar(); renderTudo(); toast('marcado na agenda');
    }});
}
