# Credits
- [Sudachi.rs](https://github.com/WorksApplications/sudachi.rs) Morphological analyzer
- [Nazeka](https://github.com/wareya/nazeka) Original deconjugation rules, deconjugator
- [JL](https://github.com/rampaa/JL/tree/master) Updated deconjugation rules, deconjugator port
- [Ichiran](https://github.com/tshatrov/ichiran) Parser tests
- [JMDict](https://www.edrdg.org/wiki/index.php/JMdict-EDICT_Dictionary_Project) Dictionary
- [JmdictFurigana](https://github.com/Doublevil/JmdictFurigana) Furigana dictionary for JMDict

# Installation
Import the dictionary
```-i --xml "path/to/jmdict_dtd.xml" --dic path/to/JMdict --furi path/to/JmdictFurigana.json```

Parse decks
```-v -t 8 -p "/path/to/directories/" --deck-type "VisualNovel"```

# Parser performance & cache
Activating the cache can offer an appreciable speedup at the cost of RAM

Here's 3 scenarios, on 75 decks totaling 42millions moji, all running on 8 threads:

- Word Cache & Deconjugator cache: 316303ms / 8 GB RAM / 8m moji/min
- Word Cache only: 324542ms / 3.7 GB RAM / 7.8m moji/min
- No Cache: 502354ms / 3 GB RAM / 5m moji/min

The best option is to have the word cache only, the deconjugator only offering ~3% more speed at a great cost of RAM
