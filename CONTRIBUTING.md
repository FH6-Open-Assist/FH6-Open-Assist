# Como contribuir

Obrigado por querer melhorar o FH6 Open Assist.

## Antes de começar

- Use uma versão atual do Windows 10 ou Windows 11 x64.
- Instale o SDK do .NET 10.
- Leia as instruções do BOT que será alterado.
- Procure uma issue ou pull request existente sobre o mesmo assunto.
- Para mudanças maiores, abra primeiro uma solicitação de melhoria para alinhar o fluxo esperado.

## Preparar o projeto

1. Faça um fork do repositório.
2. Clone o seu fork.
3. Crie uma branch curta e descritiva, como `fix/skill-points-retry`.
4. Restaure e compile:

```powershell
dotnet restore .\FH6OpenAssist.csproj
dotnet build .\FH6OpenAssist.csproj -c Release --no-restore
```

## Diretrizes

- Mantenha os textos da interface e documentação em português do Brasil.
- Preserve alterações locais e evite refatorações não relacionadas.
- Não envie `bin`, `obj`, diagnósticos, logs, credenciais ou dados pessoais.
- Explique mudanças de temporização e informe o FPS e a tela inicial usados na calibração.
- Entradas devem sempre liberar teclas em cancelamentos e falhas.
- O modo em segundo plano deve continuar sem trazer o jogo para a frente.
- Mudanças dependentes do jogo devem informar como foram testadas.

## Enviar o pull request

Abra o PR contra a branch `main` e preencha o template. O build automático precisa passar e as conversas precisam estar resolvidas. Um mantenedor revisará a alteração antes do merge.

O projeto usa **squash merge**, portanto o título do PR deve resumir claramente a mudança final.

## Validar distribuição e release

Mudanças no empacotamento devem ser verificadas com o Inno Setup 6 instalado:

```powershell
.\scripts\build-release.ps1 -Version 0.0.0-local
```

O comando deve gerar exatamente `FH6-Open-Assist-Portable.zip` e `FH6-Open-Assist-Setup.exe` a partir do mesmo staging publicado. O marcador `portable.marker` deve existir apenas no ZIP. Tags no formato `v*` acionam o workflow que cria ou atualiza a GitHub Release com os dois arquivos.

Antes de declarar que uma versão não possui pré-requisitos adicionais, ainda é obrigatório validá-la em um Windows limpo e confirmar a presença ou necessidade do Microsoft Visual C++ Redistributable.

## Segurança

Não abra uma issue pública para vulnerabilidades ou credenciais expostas. Use **Security → Report a vulnerability** no repositório para falar em privado com os mantenedores.
