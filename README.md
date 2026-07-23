# Azure Template Repo README

## Azure Developer CLIのセットアップ

以下のコマンドを実行して、Azure Developer CLI (azd) をインストールします。

```bash
curl -fsSL https://aka.ms/install-azd.sh | bash
```

インストール方法は[公式ドキュメント](https://learn.microsoft.com/ja-jp/azure/developer/azure-developer-cli/install-azd)を参照してください。

### Azure Developer CLIの動作確認

以下のコマンドでAzure Developer CLIのバージョンを確認します。

```bash
azd version
```

### Azure Developer CLIでログインする

環境変数 `AZURE_TENANT_ID`が設定されている場合は、以下のコマンドでログインします。

```bash
azd auth login --tenant-id $AZURE_TENANT_ID
```

環境変数が設定されていない場合は、以下のコマンドでログインします。

```bash
azd auth login
```

## Azure CLIをセットアップする

以下のコマンドを実行して、Azure CLIをインストールします。

```bash
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
```

インストール方法は[公式ドキュメント](https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli-linux?pivots=apt)を参照してください。

## Azure CLIでログインする

環境変数 `AZURE_TENANT_ID`が設定されている場合は、以下のコマンドでログインします。

```bash
az login --tenant $AZURE_TENANT_ID
```

### Azure CLIの動作確認

以下のコマンドでAzure CLIのバージョンとアカウント情報を確認します。

```bash
az version
az account list
```

## GitHub Codespacesの設定

`.env`でシークレットを管理する場合、以下のコマンドでCodespacesにシークレットを設定します。

```bash
gh secret set --app codespaces -f .env
```

シークレットの一覧を確認するには、以下のコマンドを実行します。

```bash
gh secret list --app codespaces
```

単一のシークレットを設定するには、以下のコマンドを使用します。

```bash
gh secret set --app codespaces SECRET_NAME
```

シークレットの削除は以下のコマンドで行います。

```bash
gh secret delete --app codespaces SECRET_NAME
```
