# Debug logging

> Quando um bug acontece no seu Pi durante uma sessão noturna, o
> jeito mais rápido de o desenvolvedor entender é olhando um log
> completo do que o servidor + o seu navegador estavam fazendo.
> Este painel resolve isso com um clique.

## O que é

Polaris coleta em um único lugar todos os eventos relevantes da
sessão atual:

| O que entra no log | De onde vem |
|---|---|
| Toda chamada HTTP (método, caminho, status, duração) | `RequestLoggingMiddleware` no servidor |
| Toda mensagem do servidor (cada `ILogger<T>` que algum serviço usa) | `LogBufferLoggerProvider` |
| Toda chamada `apiFetch` que o navegador faz | hook em `app.js` |
| Todo toast (notificação verde/amarela/vermelha no canto) | hook em `toast()` |
| Toda exceção do navegador (`window.onerror`, `unhandledrejection`) | hook global |

Os dados ficam em um anel circular de 5000 entradas em memória do
servidor. Cada navegador conectado vê o mesmo log em tempo real
através do WebSocket de status.

## Onde fica

No canto superior direito da tela, ao lado do botão `NIGHT`, tem
um botão **LOG**:

- **Cinza**: nada importante aconteceu desde a última vez que você
  abriu o painel.
- **Âmbar com número**: tem N avisos (`warn`) que você ainda não
  viu.
- **Vermelho com número**: tem N erros (`error` ou `critical`) que
  você ainda não viu.

Clique e o painel abre em tela cheia.

## O painel

```
┌─ Debug log ─────────────────────────────────────────────────┐
│ [Info+ ▾] [All sources ▾] [Search…] [Export JSONL] [Clear] [✕]│
├──────────────────────────────────────────────────────────────┤
│ 2026-05-31 02:14:11.342 [INFO ] [http] GET /api/system/status 200 4.1ms │
│ 2026-05-31 02:14:11.501 [WARN ] [server] NINA.Polaris.PHD2Client │
│                                  PHD2 disconnected, retrying...      │
│ 2026-05-31 02:14:12.000 [ERROR] [apiFetch] POST /api/camera/expose failed │
│                                  NetworkError: Failed to fetch       │
└──────────────────────────────────────────────────────────────┘
```

### Filtros

- **Nível**: descarte entradas abaixo de um patamar (`Info+`,
  `Warnings+`, `Errors only`...).
- **Source**: filtre por origem (`server`, `http`, `client`,
  `toast`, `apiFetch`, `exception`).
- **Search**: pesquisa de texto livre em mensagem, categoria e
  caminho.

### Cores

- 🔴 **error / critical** -- algo falhou.
- 🟡 **warn** -- algo está estranho mas continuou.
- 🔵 **info** -- evento normal.
- ⚪ **debug** -- detalhe verboso (raramente aparece).

### Stack traces

Quando o erro carrega um stack (exceção do servidor ou do
navegador), o painel mostra um bloco recolhido em vermelho-claro
com as primeiras 20 linhas.

## Exportar para um bug report

Clique **Export JSONL**. O navegador baixa um arquivo
`polaris-log-YYYYMMDD-HHMMSS.jsonl`, no formato
[JSON Lines](https://jsonlines.org/) (uma entrada JSON por linha).

Anexe esse arquivo ao bug report. Com ele, dá pra rastrear a
sequência exata: "às 22:14 a câmera caiu, às 22:14:05 o servidor
tentou reconectar, às 22:14:30 a próxima captura deu timeout".

Você também pode escolher **Export TXT** para um formato mais fácil
de ler num editor de texto comum.

## Limpar o log

**Clear** apaga o anel em memória do servidor. Útil quando você
quer começar uma sessão de captura zerada para isolar exatamente
qual evento causou um problema.

Pede confirmação porque é destrutivo (qualquer cliente que estava
conectado também perde o histórico anterior).

## Persistência em disco (opcional)

Por padrão, o log fica **só em memória**. Se você reiniciar
Polaris, o anel desaparece.

Em **Settings → Debug logging**, ligue **"Persist debug log to
disk"** e o servidor passa a gravar tudo em arquivos JSONL diários
em:

- **Windows**: `%LOCALAPPDATA%\NINA.Polaris\logs\polaris-YYYY-MM-DD.jsonl`
- **Linux**: `~/.local/share/NINA.Polaris/logs/polaris-YYYY-MM-DD.jsonl`

Arquivos com mais de 7 dias são removidos automaticamente para
não encher o cartão SD.

Quando ligar este modo:

- Você quer reproduzir um bug intermitente que aparece de
  madrugada e quer revisar pela manhã.
- O Pi reiniciou no meio da noite e você quer saber o que
  aconteceu antes.

Quando deixar desligado:

- Operação normal. O log em memória cobre a sessão atual e
  exportar manualmente quando precisar é mais que suficiente.
- Você quer minimizar gravação no SD card do Pi.

## Segurança e privacidade

- **Filtro de sensibilidade**: antes de qualquer entrada entrar no
  log, regexes redirecionam `password=...`, `token=...`,
  `Authorization: Bearer ...` e o cookie de sessão `polaris_session`
  para `***`. Você pode exportar e anexar o log sem se preocupar
  com vazar credencial.
- **Caminhos `/api/auth/*` perdem a query string** antes de virar
  entrada (`?token=algo` vira só `/api/auth/...`).
- **Stack traces de exceção** são truncados em 20 linhas para
  evitar que um loop de recursão use o anel inteiro com uma única
  entrada.

## Para desenvolvedores

- **`GET /api/logs?since={id}&max={n}&level={min}&source={...}&search={...}`**:
  consulta com filtros server-side.
- **`GET /api/logs/export?format=jsonl|txt`**: streaming download
  do anel inteiro.
- **`POST /api/logs/client`**: cliente envia entradas próprias
  (já é feito automaticamente pelo `app.js`).
- **`DELETE /api/logs`**: apaga o anel.

O sub-objeto `debugLog` no payload do `/ws/status` (1 Hz) carrega
entradas novas desde o último cursor de cada conexão (máx 50 por
tick) -- é assim que o painel atualiza em tempo real.
