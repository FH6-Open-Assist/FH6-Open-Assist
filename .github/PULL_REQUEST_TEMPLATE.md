## O que mudou

Descreva objetivamente a alteração e o problema que ela resolve.

## Como validar

Informe comandos, tela inicial, BOT, modo, resolução/FPS e resultado observado. Se algo não foi testado no jogo, declare explicitamente.

## Impacto em visão ou automação

Explique mudanças de OCR, visão clássica, ONNX, temporização, input ou recuperação. Use `Não se aplica` quando o PR não tocar nessas áreas.

## Checklist

- [ ] Mantive a alteração restrita ao objetivo do PR.
- [ ] Executei `dotnet restore .\FH6OpenAssist.csproj -r win-x64` e `dotnet build .\FH6OpenAssist.csproj -c Release --no-restore` com sucesso.
- [ ] Atualizei instruções ou tempos calibrados afetados pela mudança.
- [ ] Cancelamentos, falhas e perda de foco/janela continuam liberando todas as entradas.
- [ ] Não incluí credenciais, tokens, logs, diagnósticos, datasets ou prints com dados pessoais.
- [ ] Testei no Forza Horizon 6, quando a alteração depende do fluxo do jogo.
- [ ] Para visão/ONNX, mantive responsabilidades simples no detector clássico, grupos de tentativa íntegros e métricas honestas de FP/FN/Unknown.
- [ ] Para um novo modelo, regenerei ONNX e metadados juntos e verifiquei equivalência/latência sem publicar as capturas.
- [ ] Para mudanças de distribuição, validei os dois artefatos e confirmei que `portable.marker` existe apenas no ZIP.
- [ ] Não afirmei ausência de pré-requisitos sem validar em Windows limpo e verificar o Microsoft Visual C++ Redistributable.
