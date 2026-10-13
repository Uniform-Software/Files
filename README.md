<p align="center">
  <img src="src/UniFiles.App/Assets/unifiles-icon.png" width="96" alt="UniFiles" />
</p>

<h1 align="center">unifiles</h1>

<p align="center"><b>Glisse. Dépose. C'est fait.</b><br/>
Images, PDF, vidéos et noms de fichiers, traités en local. Sans compte, sans pub, sans limite de taille.</p>

<p align="center">100 % local · Open source · GPL v3 · Gamme UNI (Uniform Systems)</p>

---

UniFiles remplace les sites de conversion en ligne par une application de bureau gratuite pour Windows, macOS et Linux. **Aucun fichier ne quitte ton ordinateur** et l'appli n'envoie aucune télémétrie. Tes fichiers d'origine ne sont jamais modifiés : les résultats sont enregistrés dans un dossier de sortie (`Documents/UniFiles` par défaut).

*English: UniFiles is a free, open-source desktop app that resizes, compresses and converts images, PDFs, videos and file names locally. Nothing leaves your computer. The interface is available in French and English.*

## Ce que fait la v0.1

| Module | Actions |
| --- | --- |
| **Images** | Compresser · Redimensionner · Convertir (PNG, JPG, WebP, AVIF) · Supprimer les métadonnées |
| **Renommage** | Renommer un lot par modèle (`{name}`, `{n}`, `{date}`, `{ext}`) avec aperçu avant/après |
| **PDF** | Fusionner · Découper (par page ou par plages) · Images vers PDF · Compresser |
| **Vidéo / audio** | Couper (éditeur de montage simplifié) · Compresser · Convertir (MP4, WebM, MOV, MKV) · Extraire / convertir l'audio |

- Glisser-déposer de fichiers ou de dossiers entiers, ou fichiers passés en ligne de commande (dépôt sur l'icône du bureau).
- **Clic droit dans l'explorateur** (Windows, à activer dans les Préférences) : sous-menu « UniFiles » avec des actions rapides, lancées en arrière-plan avec les derniers réglages utilisés, et une entrée « Plus d'options… » qui ouvre l'appli. Sous Windows 11, il se trouve dans « Afficher plus d'options ».
- **Zone de notification** : fermer la fenêtre garde UniFiles actif. Le menu de l'icône affiche les lots en cours et permet de tout mettre en pause, tout reprendre, ouvrir le dossier de sortie ou quitter. Une notification s'affiche à la fin des traitements faits en arrière-plan.
- Instance unique : les lancements suivants (clic droit sur 20 fichiers = 20 lancements) transmettent leurs fichiers à l'appli déjà ouverte par un canal local (named pipe), qui en fait un seul lot.
- L'appli ne propose que les actions compatibles avec les fichiers déposés.
- File d'attente en arrière-plan : progression, pause, annulation, réordonnancement. Un fichier en échec n'arrête pas le lot, et il peut être relancé avec « Réessayer ».
- **Éditeur de coupe vidéo** : lecteur avec le son, pellicule de vignettes, tête de lecture. On coupe à la tête de lecture (`S`), on retire ou garde chaque passage (`Suppr`), on annule (`Ctrl+Z`). L'aperçu saute les passages retirés, et l'export recolle les passages gardés (coupe précise, ou export rapide sans réencodage pour un seul passage).
- Estimation du gain de place avant de lancer, résumé à la fin.
- Historique des traitements, préférences (dossier de sortie, thème sombre ou clair, langue, animations réduites).
- Accessibilité : navigation au clavier, libellés pour les lecteurs d'écran, cibles de 44 px, animations désactivables.

## Programmes externes

Les modules **Vidéo / audio** et **Compresser un PDF** s'appuient sur [ffmpeg](https://ffmpeg.org) et [Ghostscript](https://ghostscript.com). Pour l'instant, ils ne sont pas inclus dans l'appli. UniFiles les cherche, dans cet ordre :

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

Pour utiliser l'appli en mode portable ou en développement sans toucher à tes préférences, définis `UNIFILES_DATA_DIR` vers un autre dossier : les préférences et l'historique y seront stockés.

## Architecture

```
src/
  UniFiles.Core/      Logique métier, sans interface
    Tools/            IFileTool et un fichier par module (Images, Pdf, Video, Rename)
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

C# / .NET 10 · Avalonia UI 11 · CommunityToolkit.Mvvm · NetVips (libvips) · PDFsharp · FFMpegCore + ffmpeg · LibVLCSharp + LibVLC · Ghostscript · Microsoft.Data.Sqlite

Polices : Bricolage Grotesque et Figtree, sous licence OFL (voir `src/UniFiles.App/Assets/Fonts`).

## Feuille de route

- [x] Menu clic droit sous Windows
- [ ] Menu clic droit sous macOS (actions rapides du Finder) et Linux (Dolphin, Nautilus)
- [x] Icône et menu dans la zone de notification
- [ ] Notifications natives du système, lancement au démarrage
- [x] Instance unique (les autres points d'entrée transmettent leurs fichiers par named pipe)
- [ ] ffmpeg et Ghostscript embarqués
- [ ] Installeurs et mises à jour automatiques (Velopack)
- [ ] Aperçu visuel avant/après pour les images

## Licence

UniFiles est distribué sous licence [GNU GPL v3](LICENSE). Toute nouvelle dépendance doit être compatible avec la GPL v3 avant d'être ajoutée.
