# FH6 Open Assist

Assistente de automação de código aberto para **Forza Horizon 6**, feito para reduzir tarefas repetitivas de farm no Windows.

> [!IMPORTANT]
> Este é um projeto independente, não oficial e sem vínculo com Microsoft, Xbox ou Playground Games. O uso de automações pode contrariar as regras do jogo. Use por sua conta e risco.

## Baixar e executar (modo fácil)

1. Abra a página de [Releases](https://github.com/FH6-Open-Assist/FH6-Open-Assist/releases/latest).
2. Baixe o arquivo `FH6-Open-Assist-win-x64.zip`.
3. Extraia o ZIP para uma pasta comum, como `Documentos\FH6 Open Assist`.
4. Para usar o modo em segundo plano, instale o [ViGEmBus oficial](https://github.com/nefarius/ViGEmBus/releases/latest) e reinicie o computador.
5. Abra o Forza Horizon 6 e deixe-o na tela indicada nas instruções do BOT.
6. Execute `FH6OpenAssist.exe`.

O pacote publicado é autossuficiente: não é necessário instalar o .NET para apenas executar o programa.

### Como usar

1. Selecione um dos três BOTs.
2. Escolha **Primeiro plano** (mais confiável) ou **Segundo plano (teste)**.
3. Clique no botão vermelho **Instruções** e prepare o jogo.
4. Clique em **Ativar BOT**.
5. Use os atalhos globais:

| Atalho | Ação |
|---|---|
| `F8` | Inicia ou pausa o BOT ativo |
| `F9` | Encerra e desativa o BOT |

Ao pausar, todas as teclas são liberadas com segurança. Ao pressionar `F8` novamente, o fluxo atual é iniciado novamente.

> [!NOTE]
> No modo em segundo plano, o Forza pode ficar coberto por outras janelas, mas não deve ser minimizado. Esse modo usa captura de janela e um controle Xbox virtual e ainda é experimental.

## Instruções dos BOTs

### 1 - Skill Points

- Selecione o **Subaru Impreza 22B-STI Version** — de preferência com a árvore de habilidades desbloqueada.
- Ative todas as assistências.
- Vá para a rua.
- Não ative este BOT dentro da garagem.

### 2 - CR Glitch

- Selecione o **Nissan S-Cargo S1 800**, sem tunagem.
- Desative todas as assistências.
- Coloque a dificuldade em **Imbatível**.
- Vá para a rua.
- Não ative este BOT dentro da garagem.

### 3 - WheelSpin Mad Mike

- É necessário ser **VIP**.
- Tenha mais de **100.000 CR** e mais de **30 SP**.
- Esteja na garagem, no menu **Campanha**.

## Requisitos e observações

- Windows 10 ou Windows 11, arquitetura x64.
- Interface do jogo em português do Brasil.
- Resolução 16:9 recomendada.
- O Forza deve estar aberto antes da execução.
- O modo de primeiro plano interfere no teclado/mouse enquanto o BOT trabalha.
- O modo de segundo plano requer o ViGEmBus e não funciona com o jogo minimizado.
- Atualizações do jogo, mudanças de resolução e tempos de carregamento podem exigir nova calibração.

## Executar pelo código-fonte

Esta seção é para desenvolvedores. Instale o [SDK do .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) e execute:

```powershell
git clone https://github.com/FH6-Open-Assist/FH6-Open-Assist.git
cd FH6-Open-Assist
dotnet restore
dotnet run -c Release --project .\ForzaFarm.csproj
```

Para gerar um pacote autossuficiente do Windows:

```powershell
dotnet publish .\ForzaFarm.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

## Diagnóstico

O painel **Log da execução** mostra cada etapa realizada. Em uma falha de reconhecimento, o programa informa o estado esperado e pode salvar uma imagem na pasta `diagnostics` ao lado do executável. Ao relatar um erro, envie o trecho do log e a imagem correspondente, sem incluir dados pessoais.

## Contribuir

Pull requests são bem-vindos. Leia o [guia de contribuição](CONTRIBUTING.md), abra uma [issue](https://github.com/FH6-Open-Assist/FH6-Open-Assist/issues) para problemas reproduzíveis ou use as [Discussões](https://github.com/FH6-Open-Assist/FH6-Open-Assist/discussions) para dúvidas e ideias.

### ☕ Apoie o FH6 Open Assist

Se este projeto te ajudou, considere contribuir com um cafezinho. ❤️<br>
O apoio é totalmente voluntário e ajuda a manter o projeto atualizado.

**PIX:** `48bf874c-3e3d-48d1-89eb-4cd11b679167`

<p align="center">
  <img src="docs/pix-qrcode.png" alt="QR Code PIX para apoiar o FH6 Open Assist" width="430">
</p>

## Licença

Distribuído sob a [GNU General Public License v3.0](LICENSE).
