<p align="center">
  <img src="src/UniFiles.App/Assets/unifiles-icon.png" width="96" alt="UniFiles" />
</p>

<h1 align="center">unifiles</h1>

<p align="center"><b>Glisse. Dépose. C'est fait.</b><br/>
Images, PDF, vidéos et noms de fichiers, traités en local. Sans compte, sans pub, sans limite de taille.</p>

<p align="center">100 % local · Open source · GPL v3 · Gamme UNI (Uniform Systems)</p>

---

UniFiles remplace les sites de conversion en ligne par une application de bureau gratuite pour Windows, macOS et Linux. **Aucun fichier ne quitte ton ordinateur** et l'appli n'envoie aucune télémétrie. Tes fichiers d'origine ne sont jamais modifiés (seul un dossier surveillé peut les ranger ou les mettre à la corbeille, si tu le choisis) : les résultats sont enregistrés dans un dossier de sortie (`Documents/UniFiles` par défaut).

*English: UniFiles is a free, open-source desktop app that resizes, compresses and converts images, PDFs, videos and file names locally. Nothing leaves your computer. The interface is available in French and English.*

## Ce que fait la v0.3

| Module | Actions |
| --- | --- |
| **Images** | Compresser (selon une qualité ou pour une taille cible) · Redimensionner · Convertir (PNG, JPG, WebP, AVIF) · Convertir des photos iPhone (HEIC) · Supprimer les métadonnées |
| **Renommage** | Renommer un lot par modèle (`{name}`, `{n}`, `{date}`, `{ext}`) avec aperçu avant/après |
| **PDF** | Fusionner · Découper (par page ou par plages) · Organiser les pages (réordonner, pivoter, retirer) · Images vers PDF · PDF vers images · Compresser · Supprimer les métadonnées · Remplir un formulaire et ajouter des champs · Aplatir un formulaire · Exporter les réponses en CSV |
| **Vidéo / audio** | Couper (éditeur de montage simplifié, export en vidéo ou en GIF animé) · Compresser (selon une qualité ou pour une taille cible : 10 Mo pour Discord, 25 Mo pour un e-mail…) · Convertir (MP4, WebM, MOV, MKV) · Extraire / convertir l'audio · Normaliser le volume · Couper le son |
| **Fichiers** | Créer une archive ZIP (arborescence conservée) |

**Nouveau en 0.3** : formulaires PDF (remplir, ajouter des champs, aplatir, exporter en CSV), enchaînements d'actions, dossiers surveillés, choix du dossier de sortie au lancement (arborescence conservée), coller pour traiter (Cmd/Ctrl+V), « Refaire » depuis l'historique, et cinq nouvelles actions.

**En 0.2** : préréglages, tailles cibles, HEIC, aperçu avant/après des images, organisation des pages PDF, GIF animé, encodage vidéo matériel, Actions rapides du Finder et mises à jour automatiques.

- Glisser-déposer de fichiers ou de dossiers entiers, ou fichiers passés en ligne de commande (dépôt sur l'icône du bureau).
- **Préréglages** : enregistre une action et ses réglages sous un nom (« Site web 1600 px »), puis relance-les en un clic depuis l'écran d'action ou le clic droit.
- **Aperçu avant/après des images** : le vrai résultat sur un fichier, avec une poignée à faire glisser, le poids réel et un mode 100 % pour juger les détails.
- **Clic droit dans l'explorateur** (à activer dans les Préférences) : sous Windows, sous-menu « UniFiles » avec des actions rapides (et tes préréglages), lancées en arrière-plan avec les derniers réglages utilisés, et une entrée « Plus d'options… » qui ouvre l'appli ; sous Windows 11, il se trouve dans « Afficher plus d'options ». Sous macOS, les mêmes actions apparaissent dans « Actions rapides » du clic droit du Finder.
- **Encodage vidéo matériel** : la puce vidéo de l'ordinateur (Apple VideoToolbox, NVIDIA NVENC, Intel Quick Sync, AMD AMF) compresse, convertit et coupe plusieurs fois plus vite ; UniFiles teste la puce au démarrage et repasse sur x264 si elle échoue.
- **Mises à jour automatiques** depuis les versions GitHub : téléchargées en arrière-plan, installées quand on quitte l'appli (désactivable dans les Préférences). C'est le seul accès réseau de UniFiles.
- **Zone de notification** : fermer la fenêtre garde UniFiles actif. Le menu de l'icône affiche les lots en cours et permet de tout mettre en pause, tout reprendre, ouvrir le dossier de sortie ou quitter. Une notification s'affiche à la fin des traitements faits en arrière-plan.
- Instance unique : les lancements suivants (clic droit sur 20 fichiers = 20 lancements) transmettent leurs fichiers à l'appli déjà ouverte par un canal local (named pipe), qui en fait un seul lot.
- L'appli ne propose que les actions compatibles avec les fichiers déposés.
- File d'attente en arrière-plan : progression, pause, annulation, réordonnancement. Un fichier en échec n'arrête pas le lot, et il peut être relancé avec « Réessayer ».
- **Éditeur de coupe vidéo** : lecteur avec le son, pellicule de vignettes, tête de lecture. On coupe à la tête de lecture (`S`), on retire ou garde chaque passage (`Suppr`), on annule (`Ctrl+Z`). L'aperçu saute les passages retirés, et l'export recolle les passages gardés (coupe précise, ou export rapide sans réencodage pour un seul passage), en vidéo ou en GIF animé (palette calculée pour le clip, largeur et images par seconde au choix).
- **Organiseur de pages PDF** : vignettes de toutes les pages, à déplacer par glisser-déposer ou avec les boutons, à faire pivoter ou retirer, puis un nouveau PDF.
- Estimation du gain de place avant de lancer, résumé à la fin.
- Historique des traitements, préférences (dossier de sortie, thème sombre ou clair, langue, animations réduites).
- Accessibilité : navigation au clavier, libellés pour les lecteurs d'écran, cibles de 44 px, animations désactivables.

## Installer (Windows)

1. Télécharge `UniFiles-win-Setup.exe` depuis la page des versions.
2. Lance-le : UniFiles s'installe pour ton compte, sans droits administrateur, et crée un raccourci sur le bureau et dans le menu Démarrer.
3. Cette version n'est pas encore signée : si Windows affiche « Windows a protégé votre ordinateur », clique sur « Informations complémentaires », puis sur « Exécuter quand même ».

Une version portable (`UniFiles-win-Portable.zip`) est aussi disponible : décompresse-la et lance `UniFiles.exe`.
Pour désinstaller : Paramètres Windows → Applications → UniFiles. Le menu clic droit est retiré automatiquement.

L'installeur Windows embarque ffmpeg 9.0.2, Ghostscript 10.08.0 et LibVLC : rien d'autre à installer.

## Installer (macOS)

macOS 14 (Sonoma) ou plus récent. Deux versions : `osx-arm64` pour les Mac Apple Silicon (M1 et suivants), `osx-x64` pour les Mac Intel.

1. Télécharge `UniFiles-osx-arm64.dmg` (ou `UniFiles-osx-x64.dmg`) depuis la page des versions.
2. Ouvre-le et glisse UniFiles sur le raccourci Applications.
3. Lance UniFiles. Cette version n'est pas encore signée par Apple, donc macOS refuse d'abord de l'ouvrir : va dans Réglages Système → Confidentialité et sécurité, clique sur « Ouvrir quand même », puis relance l'appli.

Sont aussi disponibles un installeur (`UniFiles-osx-arm64-Setup.pkg`, même manipulation au premier lancement) et une version portable (`UniFiles-osx-arm64-Portable.zip`).

- Ouvre des fichiers depuis le Finder avec « Ouvrir avec → UniFiles », ou glisse-les sur l'icône du Dock.
- Fermer la fenêtre garde UniFiles actif dans la barre des menus ; un clic sur l'icône du Dock la rouvre. Cmd+Q quitte.
- ffmpeg 9.0.2 et LibVLC sont inclus. Ghostscript ne l'est pas (il n'existe pas de version officielle pour Mac) : pour **Compresser un PDF**, installe-le avec [Homebrew](https://brew.sh) (`brew install ghostscript`).

## Programmes externes

Les modules **Vidéo / audio** et **Compresser un PDF** s'appuient sur [ffmpeg](https://ffmpeg.org) et [Ghostscript](https://ghostscript.com). Les installeurs les embarquent (sauf Ghostscript sur macOS) ; en développement, ils ne sont pas inclus. UniFiles les cherche, dans cet ordre :

1. dans un dossier `tools/` à côté de l'exécutable ;
2. dans le `PATH` ;
3. dans les dossiers d'installation habituels (`C:\Program Files\gs\…`, `/opt/homebrew/bin`, `/usr/bin`…).

S'ils sont absents, l'action concernée affiche un message clair et ne peut pas être lancée.

Le lecteur de l'éditeur vidéo utilise [LibVLC](https://www.videolan.org/vlc/libvlc.html) (LGPL 2.1+), fourni par NuGet sous Windows et macOS. Sous Linux, installe le paquet `vlc` (ou `libvlc5`) ; sans lui, l'éditeur fonctionne quand même avec la pellicule, mais sans lecture. Tous les autres outils fonctionnent sans dépendance externe (libvips est fourni par NuGet).

## Compiler et lancer

Prérequis : [SDK .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/UniFiles.App
```

Pour lancer les tests :

```bash
dotnet test
```

Options de ligne de commande : `UniFiles fichiers…` (ouvre l'appli sur ces fichiers), `--action <id> fichiers…` (action rapide en arrière-plan, ex. `--action image.compress`), `--register` / `--unregister` (ajoute ou retire le menu clic droit, pour les installeurs).

### Publier une version Windows

```powershell
./build/release-windows.ps1 -Version 0.2.0
```

Le script lance les tests, publie l'appli (autonome, win-x64), ajoute ffmpeg (build BtbN figé et vérifié par SHA-256) et Ghostscript avec leurs licences, puis crée l'installeur et les paquets de mise à jour avec [Velopack](https://velopack.io) dans `artifacts/releases`. Les notes de version sont lues dans `build/release-notes/<version>.md` (français) ; la version anglaise, `<version>.en.md`, est affichée dans l'appli en anglais et ajoutée à la version GitHub. Prérequis : Ghostscript 10.08.0 installé sur la machine de build.

### Publier une version macOS

```bash
./build/release-macos.sh 0.2.0 --arch arm64
```

À lancer sur un Mac, une fois par architecture (`--arch arm64` ou `--arch x64`, par défaut celle de la machine). Le script lance les tests, publie l'appli (autonome, `osx-arm64` ou `osx-x64`), ajoute ffmpeg et ffprobe (builds statiques de Martin Riedl, figés et vérifiés par SHA-256), crée l'icône et l'`Info.plist` (types de fichiers acceptés par le Finder), puis le `.pkg`, la version portable et les paquets de mise à jour avec Velopack, et enfin une image disque `.dmg` mise en forme (fond et flèche de `build/dmg`, l'appli et un raccourci vers Applications, via [dmgbuild](https://github.com/dmgbuild/dmgbuild), installé automatiquement ; prérequis : `python3`), dans `artifacts/releases/osx-<arch>`. Les versions suivent SemVer : `0.2.0`, ou `0.2.0-alpha` pour une préversion. Chaque architecture a son propre canal de mise à jour. Sans certificat, l'appli reçoit une signature ad hoc ; avec un compte Apple Developer, passe `--sign-app "Developer ID Application: …"`, `--sign-installer "Developer ID Installer: …"` et `--notary-profile <profil notarytool>` pour signer et notariser.

### Publier avec GitHub Actions

Chaque push et chaque pull request lancent les tests sous Windows et macOS (`.github/workflows/ci.yml`). Pour publier une version, pousse un tag :

```bash
git tag v0.2.0 && git push origin v0.2.0
```

`.github/workflows/release.yml` construit alors l'installeur Windows et les deux versions macOS (DMG, `.pkg`, versions portables), puis crée la version GitHub avec les notes de `build/release-notes/<version>.md` et les paquets de mise à jour. Un tag avec suffixe (`v0.3.0-beta`) devient une préversion, proposée en mise à jour seulement à ceux qui utilisent déjà une préversion.

Pour utiliser l'appli en mode portable ou en développement sans toucher à tes préférences, définis `UNIFILES_DATA_DIR` vers un autre dossier : les préférences et l'historique y seront stockés. Pour tester les mises à jour sans GitHub, définis `UNIFILES_UPDATE_URL` vers un dossier (ou une adresse web) contenant les paquets produits par les scripts de publication.

## Architecture

```
src/
  UniFiles.Core/      Logique métier, sans interface
    Tools/            IFileTool et un fichier par module (Images, Pdf, Video, Rename, Files), ChainTool
    Automation/       Dossiers surveillés (FolderWatcher, WatchRule)
    Jobs/             JobQueue : exécution parallèle, pause, annulation, erreurs isolées
    Storage/          Préférences (JSON) et historique (SQLite)
    Resources/        Tous les textes : Strings.resx (anglais) et Strings.fr.resx
  UniFiles.App/       Interface Avalonia 11, MVVM (CommunityToolkit.Mvvm)
    Platform/         Ce qui dépend du système, derrière des interfaces (menu clic droit, notifications)
    Services/         Instance unique, lancement, dialogues, langue
    Themes/           Design system : couleurs, rayons et icônes en ressources, un dictionnaire par thème
    ViewModels/ Views/ Controls/
tests/
  UniFiles.Core.Tests/
```

**Ajouter un outil** revient à écrire une classe qui implémente `IFileTool` (ou hérite de `FileToolBase`), puis à l'enregistrer dans `ToolCatalog`. L'écran de réglages se construit à partir de ses `Options` (liste, curseur, interrupteur, texte), sans toucher à l'interface.

## Stack

C# / .NET 10 · Avalonia UI 11 · CommunityToolkit.Mvvm · NetVips (libvips) · PDFsharp · PDFium · FFMpegCore + ffmpeg · LibVLCSharp + LibVLC · Ghostscript · Microsoft.Data.Sqlite

Polices : Bricolage Grotesque et Figtree, sous licence OFL (voir `src/UniFiles.App/Assets/Fonts`).

## Feuille de route

- [x] Menu clic droit sous Windows
- [x] Actions rapides du Finder (macOS)
- [ ] Menu clic droit sous Linux (Dolphin, Nautilus)
- [x] Icône et menu dans la zone de notification
- [ ] Notifications natives du système
- [x] Lancement à l'ouverture de session (dossiers surveillés)
- [x] Instance unique (les autres points d'entrée transmettent leurs fichiers par named pipe)
- [x] ffmpeg et Ghostscript embarqués (Windows), ffmpeg embarqué (macOS)
- [x] Installeur Windows (Velopack)
- [x] Mises à jour automatiques (GitHub Releases, Velopack)
- [x] Publication automatique par GitHub Actions
- [ ] Signature de l'installeur Windows
- [x] Installeur macOS (Velopack, Apple Silicon et Intel)
- [ ] Signature et notarisation Apple
- [ ] Ghostscript embarqué sous macOS
- [ ] Installeur Linux
- [x] Aperçu visuel avant/après pour les images
- [x] Préréglages, tailles cibles, HEIC, organisation des pages PDF, GIF, encodage matériel
- [x] Formulaires PDF, enchaînements, dossiers surveillés
- [ ] Signature numérique des PDF avec certificat
- [ ] Éditeur de champs complet (listes, validation, calculs)

## Licence

UniFiles est distribué sous licence [GNU GPL v3](LICENSE). Toute nouvelle dépendance doit être compatible avec la GPL v3 avant d'être ajoutée.
