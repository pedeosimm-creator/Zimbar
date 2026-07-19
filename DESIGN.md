# ZIMBAR DESIGN SYSTEM — NEOBRUTALISMO v1

> Guideline oficial do visual do Zimbar. Toda UI nova segue ESTE documento.
> Código: os tokens viram resources no `App.xaml`/`Theme.cs` e helpers em `DesignSystem.cs` (`Zui.*`).

---

## 1. Princípios

1. **Traço preto em tudo.** Todo bloco, botão, campo e card tem contorno de tinta (`Ink`) de 2–3px. Nada "flutua" sem borda.
2. **Sombra dura, nunca blur.** Drop shadow preta, opacidade 100%, deslocada em X **e** Y (3–6px), blur **zero**. No WPF isso é feito com **borda dupla** (um bloco de tinta atrás do bloco de cor), nunca com `DropShadowEffect` em container com conteúdo (causa fantasma de texto).
3. **Cor é estrutura.** Seções e cards se diferenciam por COR CHAPADA, não por tons de cinza. Cores **alternam** (nunca dois blocos vizinhos da mesma cor).
4. **Sem gradiente. Sem glow. Sem vidro.** Superfícies 100% opacas.
5. **Tipografia grande e grotesca.** Títulos em display pesada; rótulos em mono maiúscula; o resto em texto normal. Contraste de escala é decoração.
6. **Formas cruas.** Cantos levemente arredondados (4–12px), nunca pílula (999px), nunca reto demais (0px só em janelas sticky).
7. **Interação física.** Hover realça; clique **afunda** (o bloco desce sobre a própria sombra).

## 2. Cor

### Tinta (fixa, todos os temas)
| Token | Hex | Uso |
|---|---|---|
| `Ink` | `#111111` | bordas, sombras duras, texto principal, tab ativa |
| `TextDim` | `#4A4458` | texto secundário |
| `TextDone` | `#8B8598` | texto apagado/meta |

### Papel (fundo da janela — muda por tema)
| Tema | Paper | Accent | AccentDeep |
|---|---|---|---|
| Roxo | `#E6DBFF` | `#A78BFA` | `#7C3AED` |
| Azul | `#CFE8FF` | `#6EC1FF` | `#1D9BF0` |
| Verde | `#D3F5DC` | `#6EE7A0` | `#16A34A` |
| Rosa | `#FFD9EC` | `#FF8FC2` | `#EC4899` |
| Âmbar | `#FFEDB8` | `#FFD34D` | `#F59E0B` |

### Blocos vibrantes (paleta de alternância — fixa)
`#FDFD96` amarelo · `#90EE90` lima · `#FFB2EF` rosa · `#C4A1FF` roxo · `#87CEEB` azul · `#FFA07A` coral

- Superfície de conteúdo neutra: **branco** `#FFFFFF`.
- Semáforo do plano: difícil `#FF6B6B` · média `#FFDB58` · fácil `#90EE90`.
- Regra de alternância: blocos irmãos ciclam a paleta na ordem acima (`Zui.Tint(i)`).

## 3. Tipografia

| Papel | Fonte | Peso/Caixa | Tamanho |
|---|---|---|---|
| Display (hero, títulos de card) | **Bahnschrift** | Bold | 15–30px |
| Corpo | Segoe UI Variable Text / Segoe UI | Regular/SemiBold | 12–14px |
| Rótulo HUD / tag / data | **Cascadia Mono** | Bold, MAIÚSCULA | 9–11px |

- Hero do Painel: Bahnschrift Bold ~30px, cor `Ink`, com **bloco de accent atrás de uma palavra** (marca-texto).
- Rótulos de seção são **TAGS**: bloquinho de tinta (fundo `Ink`, texto branco mono) ou bloco de cor com borda.

## 4. Forma e sombra

| Elemento | Radius | Borda | Sombra (offset) |
|---|---|---|---|
| Janela principal | 14 | 3px Ink | 8px |
| Card / bloco | 10 | 2px Ink | 4px |
| Botão primário | 9 | 2px Ink | 3px |
| Chip / tab | 8 | 1.5–2px Ink | 2px (só primário tem) |
| Campo de texto | 8 | 2px Ink | — (foco: borda vira Accent) |
| Sticky note | 0 | 2.5px Ink | — |

**Implementação da sombra dura (obrigatória):** `Zui.Block(...)` = Grid com 2 Borders — o de trás é `Ink` deslocado (margin `o,o,0,0`), o da frente é o bloco (margin `0,0,o,o`). Zero `DropShadowEffect` em containers de conteúdo.

## 5. Componentes

- **Bloco/Card** (`Zui.Block`): fundo branco ou cor da paleta, borda 2px, sombra 4px. Hover (se clicável): borda vira `AccentDeep`.
- **Botão primário** (`PrimaryBtn`): bloco accent, texto Ink bold, sombra 3px; **press = afunda** (bloco desce sobre a sombra, sombra some).
- **Chip** (`Chip`): bloco branco, borda 2px, sem sombra; hover pinta de accent claro.
- **Tab de navegação** (`NavBtn`): repouso = texto tinta sem fundo; hover = bloco branco com borda; **ativa = bloco de TINTA com texto branco** (o "selecionado preto" do neobrutalismo).
- **Tag de seção** (`Zui.Tag`): mono maiúscula branca em bloquinho `Ink` radius 4 (ou variante colorida com borda).
- **Campo** (`InlineAdd`): branco, borda 2px `Ink` @40%, foco = borda `AccentDeep` 2px.
- **Badge de data** (EventoRow): bloquinho de cor com borda 1.5px Ink, texto mono Ink.
- **Barra de progresso**: trilho branco borda 2px Ink, preenchimento accent chapado, altura ≥ 10px.
- **Scrollbar**: thumb `Ink` @35%, hover `Ink`, 8px.
- **Popup**: papel do tema, borda 2.5px Ink, sombra dura 6px (aqui pode `DropShadowEffect` porque o fundo é opaco).

## 6. NUNCA fazer

- ❌ Gradiente, glow, blur, transparência em superfície.
- ❌ Sombra cinza ou com opacidade < 100%.
- ❌ `DropShadowEffect` num container com texto e fundo translúcido (fantasma).
- ❌ Dois blocos vizinhos da mesma cor.
- ❌ Borda fina cinza (#EEE) — borda é TINTA ou não existe.
- ❌ Pílula 999px; caixa de texto visível quando um botão-revelar resolve.
- ❌ Duas fileiras de abas; aba escondida por falta de espaço (cai pra ícone).

## 7. Mapa de tokens (código)

- `Ink`, `TextMain(=Ink)`, `TextDim`, `TextDone`, `Accent`, `AccentSoft(=AccentDeep)`, `CardBg/CardBgBrush(=Paper)`, `Surface(=branco)`, `BlockYellow/Lime/Pink/Purple/Blue/Coral`, `Dificil/Media/Facil(+Bg)`.
- Fontes: `Display(=Bahnschrift)`, `Body(=Segoe UI Variable)`, `Mono(=Cascadia Mono)`.
- Helpers: `Zui.Block`, `Zui.Tag`, `Zui.Tint(i)`, `Zui.HudLabel`, `Zui.Button(kind)`, `Zui.RevealAdd`.
