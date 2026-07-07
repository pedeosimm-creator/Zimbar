# Zimbar Design System

Esta copia experimental centraliza tokens, estilos e componentes repetidos do Zimbar.

## Contrato

- Novas interfaces devem usar `Zui` e `ZTokens` em vez de criar componentes visuais manualmente no code-behind.
- Novos estilos XAML devem usar as chaves canonicas `Zimbar.*`.
- As chaves antigas (`Accent`, `Surface`, `Chip`, `InlineAdd`, etc.) continuam existindo para compatibilidade com a UI atual.
- `ThemeManager.Apply()` deve atualizar tanto as chaves antigas quanto as chaves `Zimbar.*`.

## Componentes Disponiveis

- `Zui.Button`: cria botoes `Chip`, `Primary`, `Nav` e `Ghost`.
- `Zui.HudLabel`, `Zui.SectionLabel`, `Zui.DimText`, `Zui.BodyText`: tipografia padronizada.
- `Zui.GlassCard`, `Zui.StatCard`: superficies translucidas com hover/acao.
- `Zui.InlineAddBox`, `Zui.RevealAdd`: padrao de adicionar item por Enter e cancelar por Esc.

## Estilos XAML Canonicos

- `Zimbar.Button.Chip`
- `Zimbar.Button.Primary`
- `Zimbar.Button.Nav`
- `Zimbar.Button.Ghost`
- `Zimbar.TextBox.InlineAdd`
- `Zimbar.ScrollBar.Neo`
- `Zimbar.Border.GlassPanel`
- `Zimbar.Border.CosmicCard`
- `Zimbar.Border.PopupCard`

## Revisao Obrigatoria Para Nova UI

Antes de aceitar qualquer nova tela ou componente:

1. Verifique se nao ha novo `new Border`, `new Button` ou `new TextBlock` com tokens visuais hardcoded quando um helper `Zui` atende.
2. Verifique se cores, fontes, radius, efeitos e brushes usam `ZTokens`, `FindResource` ou `DynamicResource`.
3. Rode `dotnet build --no-restore`.
4. Abra com `--show` e teste pelo menos: foco inicial, navegacao por teclado, hover/click dos botoes novos e fechamento por Esc.
5. Se a mudanca cria uma interacao repetivel, promova para `DesignSystem.cs` antes de espalhar pela tela.
