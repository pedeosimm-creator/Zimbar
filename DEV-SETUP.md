# Zimbar — setup pra codar em outro PC

Guia rápido pra deixar o ambiente pronto num computador novo e continuar
o desenvolvimento (não é o instalador do app final — é pra mexer no código).

## 1. Instalar o necessário
- **.NET 9 SDK** (não só o runtime): https://dotnet.microsoft.com/download/dotnet/9.0
  - Confirme com `dotnet --version` (tem que ser 9.x).
- **Git**: https://git-scm.com/download/win
- Um editor: **VS Code** (com extensão C# Dev Kit) ou **Visual Studio 2022**.

## 2. Pegar o código
### Opção A — GitHub (recomendado, sincroniza os 2 PCs)
```bash
git clone https://github.com/pedeosimm-creator/Zimbar.git
cd Zimbar
```
Depois disso, o fluxo entre os PCs é só:
- antes de codar:  `git pull`
- depois de codar: `git add -A && git commit -m "..." && git push`

### Opção B — a partir do zip
Extraia o `Zimbar-Codigo.zip` numa pasta qualquer. Já vem com o histórico
`.git`; se quiser ligar ao GitHub depois, é `git remote add origin <url>`.

## 3. Rodar e compilar
```bash
dotnet build                 # compila (Debug)
dotnet run -- --show         # roda já abrindo a barra pra testar
```
Sem `--show`, o app inicia só na bandeja (Ctrl+Alt+Z abre a barra,
Ctrl+Alt+D abre o ZimNotes).

Pra gerar o `.exe` self-contained (o que vai no instalador):
```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```
Sai em `bin/Release/net9.0-windows10.0.19041.0/win-x64/publish/Zimbar.exe`.

As três flags são obrigatórias — não dá pra encurtar o comando. Sem
`IncludeNativeLibrariesForSelfExtract` o exe compila e instala normalmente,
mas morre ao abrir com `DllNotFoundException` no WPF (as DLLs nativas ficam
de fora do pacote). O sintoma é feio de diagnosticar: nem chega a escrever
no `zimbar.log`, só aparece no Visualizador de Eventos.

## 4. Mapa do projeto

O C# é só a casca: janela, bandeja, hotkey e o que o navegador não faz.
A interface inteira é HTML/JS em `ui/`.

- `App.xaml.cs` — bandeja + hotkeys globais (Ctrl+Alt+Z / Ctrl+Alt+D).
- `BarWindow.xaml/.cs` — a janela do Zimbar, hospedando `ui/index.html`.
- `NotesWindow.xaml/.cs` — a janela solta do ZimNotes, hospedando
  `ui/notas.html`. Não desenha nada: é a mesma interface da aba Notas.
- `PomoWindow.cs` — pomodoro.  `Ponte.cs` — a conversa JS ⇄ C#.
- `Theme.cs` — temas + config (`%APPDATA%\Zimbar\settings.json`).
- `News.cs` — RSS das notícias (o feed não libera CORS, então é o C# que busca).

Dentro de `ui/`:
- `index.html` + `app.js` — o Zimbar (Hoje, Kanban, Agenda, Listas, Notícias, Contas).
- `dados.js` — Supabase: flowspace + contas.
- `notas.css` + `notas.js` — **o ZimNotes**. Servem ao mesmo tempo a aba
  Notas (dentro do `index.html`) e a janela solta (`notas.html`). Mexeu num,
  mudou nos dois — é de propósito, é o que faz os dois serem iguais.
- `ponte.js` — o outro lado da `Ponte.cs`.

## Observações
- Precisa de internet: os dados vêm do Supabase (chave anon já pública no site).
- Config/tema/posição ficam em `%APPDATA%\Zimbar\settings.json` (por PC).
