# ZIMBAR DESIGN SYSTEM — baseado no ACERVO

> Guideline oficial do visual do Zimbar. Derivado do **Acervo** (acervozim.netlify.app,
> fonte em `D:\acervo`), que é a referência de neobrutalismo que o Pedro validou.
> Toda UI nova segue ESTE documento.

---

## 1. Fontes (o mais importante)

| Papel | Fonte | Uso |
|---|---|---|
| **Display** | **Archivo Black** | hero, títulos de card/seção, `+ nota`, marca-texto. Peso brutal, impacto. |
| **Corpo** | **Space Grotesk** | tudo o mais: texto, labels, chips, inputs. Pesos Medium/SemiBold/Bold. |

- Embutidas em `assets/fonts/*.ttf`, referenciadas por `pack://application:,,,/assets/fonts/#Archivo Black` etc.
- NÃO usar mono (Cascadia). A chave `Mono` aponta pra Space Grotesk por compat.
- Input de digitação usa Space Grotesk (Archivo Black é pesada demais pra digitar).

## 2. Cor (paleta do Acervo)

**Neutros (fixos):**
| Token | Hex | Uso |
|---|---|---|
| `Ink` | `#161613` | tinta quente: bordas, sombras, texto, tab ativa |
| `Cream` / `CardBg` | `#f6f2e7` | fundo da janela |
| `Surface` (paper) | `#fffdf7` | superfície de cards |
| `Mist` | `#e8e1cf` | hover neutro |
| `TextDim` | `#6f6a5c` | texto secundário |
| `TextDone` | `#a39b85` | texto apagado / placeholder |

**Vibrantes (cheio + soft):** sun `#ffc940`/`#ffe9ac` · tang `#ff5f35`/`#ffd3c4` · leaf `#3ec46d`/`#c4eed4` · sky `#4d7cff`/`#cdd9ff` · grape `#7b61ff`/`#d9d1ff` · rose `#ff5c8a`/`#ffd0de`.

- **Card colorido** usa a versão SOFT de fundo; **badge de ícone** usa a versão CHEIA.
- Tema (Roxo/Azul/Verde/Rosa/Âmbar) só troca o **Accent** (um dos vibrantes). Cream/paper/ink são fixos.
- Semáforo do Hoje: difícil = tang, média = sun, fácil = leaf (fundo soft).

## 3. Forma e sombra

- **Sombra dura do Acervo: `4px 4px 0` de tinta, sem blur.** No WPF, feita por **borda dupla** (`Zui.Block`: bloco de tinta atrás + bloco de cor na frente deslocado) — NUNCA `DropShadowEffect` em container de conteúdo (fantasma de texto). Effect só na janela/popups opacos.
- Cantos: card 14–16px, botão 11px, chip/tab 10px, input 10px, badge 7–9px.
- Borda: **2px `Ink`** em tudo (card, botão, chip, input, badge). Nada de borda fina cinza.

## 4. Componentes (`.nb-*` do Acervo → WPF)

- **Card / bloco** (`Zui.Block`): paper ou soft-tint, borda 2px ink, radius 14, sombra 4px. Clicável → **hover levanta** (sobe/esquerda 2px, sombra cresce).
- **Badge de ícone** (`Zui.IconBadge`): quadrado cor cheia, borda ink, radius 9, glyph/letra no meio.
- **Botão primário** (`PrimaryBtn`): bloco accent, borda ink, sombra dura; **press AFUNDA** (desce sobre a sombra).
- **Chip** (`Chip`): paper, borda 2px ink, radius 10, weight bold; hover mist; press accent.
- **Tab** (`NavBtn`): repouso texto dim; hover bloco mist; **ativa = bloco de TINTA com texto paper** (setado no `SwitchView`).
- **Input** (`InlineAdd`): paper, borda 2px ink, radius 10; foco borda vira accent.
- **Badge de data/evento**: soft-tint (sky = evento, sun = recorrente) com borda ink 1px.

## 5. Layout por tela

- **Painel**: hero Archivo Black com uma palavra em marca-texto (bloco de accent + borda ink). Captura = cards TODOS na MESMA cor (sun-soft). Hoje/Próximos = blocos soft.
- **Kanban**: colunas = blocos com header em tag de accent; itens = **cards** paper (bolinha de status + título + data), nunca linhas soltas.
- **Agenda**: cada dia = card com borda ink (contraste real entre dias); hoje = fundo accent-soft; eventos = chips coloridos (sky/sun).
- **Links**: pastas = cards soft alternados; cada link = **badge** (favicon do DuckDuckGo, com LETRA de fallback — todo link tem badge) + nome, 1 clique abre.
- **ZimNotes**: biblioteca (cards na cor da nota) + sticky. Header com grip `⠿` + cursor SizeAll = fácil de arrastar.

## 6. NUNCA

- Gradiente, glow, blur, transparência de vidro, tema roxo-cósmico (aposentado).
- Sombra cinza/suave; `DropShadowEffect` em container com texto.
- Borda fina cinza; pílula 999px em card; caixa de texto onde um botão-revelar resolve.
- Duas fileiras de abas; aba escondida por falta de espaço (cai pra ícone).
- Cores alternadas onde deveria ser uniforme (ex: captura = 1 cor só).

## 7. Tokens (código)

`Ink`, `Cream`, `Surface`, `Mist`, `TextMain(=Ink)`, `TextDim`, `TextDone`, `Accent`, `AccentSoft`, `Sun/Tang/Leaf/Sky/Grape/Rose` (+`*Soft`), `Dificil/Media/Facil(+Bg)`. Fontes `Display(=Archivo Black)`, `Body(=Space Grotesk)`. Helpers: `Zui.Block`, `Zui.IconBadge`, `Zui.Tag`, `Zui.Tint(i)`/`TintFull(i)`, `Zui.RevealAdd`.
