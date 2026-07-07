# Revisao do Refactor de Design System

## Resultado

- Criada copia isolada em `D:\Zimbar-DesignSystem`.
- Projeto experimental renomeado para gerar `ZimbarDesignSystem.exe`.
- Adicionado `DesignSystem.cs` com `ZTokens`, `Zui` e componentes reutilizaveis.
- Adicionados aliases canonicos `Zimbar.*` para tokens e styles em `App.xaml`.
- `ThemeManager.Apply()` agora atualiza tambem os tokens `Zimbar.*`.
- Superficies principais em `BarWindow.xaml`, `NotesWindow.xaml` e `PomoWindow.xaml` usam styles do Design System.
- Helpers comuns em `BarWindow.xaml.cs` delegam para `Zui`.

## Validacao

- `dotnet restore D:\Zimbar-DesignSystem\Zimbar.csproj`: OK.
- `dotnet build D:\Zimbar-DesignSystem\Zimbar.csproj --no-restore`: OK, 0 erros, 0 warnings no build final.
- Smoke WPF com `--show`: OK.
- Navegacao por teclado `Ctrl+1..8`: OK.
- `Esc` na barra: OK.
- Processo final ficou responsivo e foi fechado apos teste.

## Revisao

O Design System agora existe como contrato reutilizavel, mas o projeto ainda tem muitos elementos de dominio criados manualmente em `BarWindow.xaml.cs`. Eles compilam e continuam funcionais, porem uma migracao 100% "sem excecoes" deve ser feita em ondas menores para evitar quebrar calendario, kanban, refs, links e news de uma vez.

Proximas extracoes recomendadas:

1. `ZSelectableChip`
2. `ZCategoryHeader`
3. `ZHoverActionsRow`
4. `ZDragDropItem`
5. `ZDateCell` / `ZDatePicker`
6. `ZNewsCard`

## Observacao de Git

`D:\Zimbar` e `D:\Zimbar-DesignSystem` nao sao repositorios Git. Portanto nao houve commit final.
