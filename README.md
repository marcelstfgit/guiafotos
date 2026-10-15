# GuiaFotos

**Português (Brasil)** | [English (US)](README.en-US.md)

Organizador de fotos para Windows, inspirado na praticidade do Picasa, com foco em encontrar pessoas e reunir versões da mesma fotografia.

**Projeto em desenvolvimento · 0.2.0 de pré-validação.**

## Por que existe

O Picasa tornou a organização de fotos no computador acessível: navegar pelas pastas, reencontrar momentos e fazer ajustes simples fazia parte da mesma experiência. Para quem cuidava de uma coleção pessoal, essa praticidade deixou saudade.

Em 2016, o Google [anunciou o encerramento do Picasa para concentrar seus esforços no Google Fotos](https://googlephotos.blogspot.com/2016/02/moving-on-from-picasa.html). O GuiaFotos começou inspirado nessa experiência, buscando combinar a organização local com recursos atuais de reconhecimento facial, comparação de imagens e backup.

## O que já está implementado

- **Pessoas:** cinco motores de reconhecimento selecionáveis, SCRFD/YuNet, busca de rostos pequenos e agrupamento com nomes, união, separação e desfazer.
- **Versões de fotos:** comparação de iluminação, compressão e resolução, visualização lado a lado e escolha da cópia preferida.
- **Duplicatas:** filtro rápido por tamanho e amostras de 16 KB com xxHash64; confirmação dos candidatos por comparação completa de bytes.
- **Biblioteca local:** álbuns, etiquetas, avaliações, favoritos, metadados e edição com exportação em cópias.
- **Nextcloud:** snapshots de backup e restauração com verificação de integridade.
- **Distribuição:** scripts para gerar instalador `.exe` e versão portátil para Windows x64.

Os modelos faciais são baixados ao ativar cada motor; a análise de rostos e semelhança ocorre localmente. Nenhuma análise exclui fotos automaticamente.

## Estado atual

Compilação cruzada e testes locais de agrupamento, cache, persistência e variantes de imagens foram executados. Ainda precisamos validar a interface e a instalação em Windows real, medir a precisão facial com fotos representativas e testar Nextcloud de produção. Os testes locais não comprovam a precisão dos modelos treinados.

A integração opcional com SSCD está preparada; o modelo não acompanha o pacote local atual e sua exportação ainda precisa ser validada. Detalhes de uso e limites estão em [INSTRUCOES.txt](INSTRUCOES.txt) e [LEIA-ME-PACOTES.txt](LEIA-ME-PACOTES.txt).

## Compilar

No Windows, instale .NET SDK 8, Python 3.11+ e 7-Zip. A primeira compilação precisa de internet para baixar dependências.

```powershell
.\build-windows.ps1
```

O executável, o instalador e o ZIP portátil são gerados em `artifacts/`. O [workflow Windows](.github/workflows/windows.yml) reúne a compilação e as verificações automatizadas; sua execução no GitHub ainda está pendente.

## Contribuir

Quer ajudar a transformar essa ideia em um organizador de fotos confiável? Contribuições de programação são bem-vindas, especialmente em reconhecimento facial, fotos semelhantes, interface Windows e testes com coleções reais.

Abra uma [issue](https://github.com/marcelstfgit/guiafotos/issues) ou envie um pull request. Consulte [CONTRIBUTING.md](CONTRIBUTING.md) para começar.

## Licença

MIT para o código do GuiaFotos. Dependências e modelos têm condições próprias, descritas em [THIRD-PARTY.txt](THIRD-PARTY.txt) e `licenses/`.
