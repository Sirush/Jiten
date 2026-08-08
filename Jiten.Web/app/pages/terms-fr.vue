<script setup lang="ts">
  const { legal } = useLegalIdentity();
  const doc = LEGAL_DOCUMENTS.cgu;

  useSeoMeta({ title: "Conditions Générales d'Utilisation" });
</script>

<template>
  <div class="legal-doc mx-auto max-w-3xl px-6 py-10 leading-relaxed text-gray-800 dark:text-gray-200">
    <h1 class="mb-2 text-center text-3xl font-bold text-gray-900 dark:text-gray-100">Conditions Générales d'Utilisation</h1>
    <p class="mb-1 text-center text-gray-600 dark:text-gray-400">
      <b>Éditeur :</b> Jitensoft, entreprise individuelle. L'identification complète de l'éditeur figure dans les
      <NuxtLink to="/mentions-legales">Mentions légales</NuxtLink>.
    </p>
    <p class="mb-1 text-center text-gray-600 dark:text-gray-400"><b>Version :</b> {{ doc.version }} — <b>Entrée en vigueur :</b> {{ doc.effectiveDateFr }}</p>
    <p class="mb-8 text-center italic text-gray-500 dark:text-gray-400">
      <NuxtLink to="/terms">English version available here</NuxtLink>. Pour les consommateurs résidant en France, la version française prévaut.
    </p>

    <p>
      Les présentes Conditions Générales d'Utilisation (les « CGU ») régissent l'accès au site <code>https://jiten.moe</code>, à ses sous-domaines, à son API
      ainsi qu'à l'extension de navigateur et aux applications compagnons publiées par Jitensoft (ensemble, le « Service »), et leur utilisation.
    </p>
    <p>
      L'achat de l'offre payante Jiten+ est en outre régi par les <NuxtLink to="/cgv-fr">Conditions Générales de Vente</NuxtLink>. En cas de contradiction
      portant sur la vente, les Conditions Générales de Vente prévalent.
    </p>
    <p>En accédant au Service ou en l'utilisant, vous acceptez les présentes CGU. À défaut d'acceptation, n'utilisez pas le Service.</p>

    <section class="mb-10">
      <h2 class="section-title">1. Définitions</h2>
      <ul>
        <li><b>Jitensoft</b>, <b>nous</b> : l'éditeur identifié dans les Mentions légales.</li>
        <li><b>Service</b> : tel que défini ci-dessus.</li>
        <li><b>Utilisateur</b>, <b>vous</b> : toute personne accédant au Service, inscrite ou non.</li>
        <li><b>Compte</b> : un compte utilisateur enregistré sur le Service.</li>
        <li><b>Jiten+</b> : l'offre payante optionnelle décrite dans les Conditions Générales de Vente.</li>
        <li>
          <b>Corpus</b> : la base de données de médias japonais, de vocabulaire, de statistiques et de données dérivées constituée par Jitensoft.
        </li>
        <li><b>Matériel Source</b> : les textes japonais de tiers analysés par le Service.</li>
        <li>
          <b>Données Dérivées</b> : les résultats statistiques et analytiques produits à partir du Matériel Source, notamment les décomptes de mots et de
          caractères, les données de fréquence, les estimations de difficulté, les listes de vocabulaire et les decks d'étude générés.
        </li>
        <li><b>Contenu Utilisateur</b> : tout élément qu'un Utilisateur soumet, téléverse ou crée au sein du Service.</li>
      </ul>
    </section>

    <section class="mb-10">
      <h2 class="section-title">2. Le Service</h2>
      <p>
        Jiten analyse des médias japonais et publie des statistiques, des listes de vocabulaire, des données de fréquence, des estimations de difficulté et
        du matériel d'étude issus de cette analyse. Le Service fournit également des outils d'apprentissage, notamment un système de répétition espacée, un
        suivi de la couverture lexicale au regard du vocabulaire propre à l'Utilisateur, l'export de decks, une extension de navigateur et des applications
        compagnons.
      </p>
      <p>
        Le Service est une aide à l'étude. Il ne constitue ni un cours, ni une certification, ni un service de traduction, ni un substitut à un enseignement
        qualifié.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">3. Conditions d'accès et Comptes</h2>
      <p>
        <b>3.1</b> Vous devez être âgé d'au moins 15 ans (âge du consentement numérique en France) ou disposer de l'autorisation d'un titulaire de l'autorité
        parentale.
      </p>
      <p>
        <b>3.2</b> Une grande partie du Service est utilisable sans Compte. Un Compte est nécessaire pour les fonctionnalités reposant sur la connaissance de
        votre vocabulaire personnel, ainsi que pour Jiten+.
      </p>
      <p>
        <b>3.3</b> Vous êtes responsable de la confidentialité de vos identifiants et des activités effectuées depuis votre Compte. Informez-nous à
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> si vous estimez que votre Compte a été compromis.
      </p>
      <p>
        <b>3.4</b> Une même personne physique ne peut détenir plusieurs Comptes aux fins de contourner les limites d'usage, les crédits promotionnels, les
        droits attachés à une période d'essai ou les mécanismes de vote sur les demandes de médias.
      </p>
      <p>
        <b>3.5</b> Vous pouvez supprimer votre Compte à tout moment depuis les paramètres de votre compte. La suppression est décrite dans la
        <NuxtLink to="/privacy">Politique de confidentialité</NuxtLink>.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">4. Gratuité</h2>
      <p><b>4.1</b> Le Service est gratuit, à l'exception de l'offre optionnelle Jiten+.</p>
      <p>
        <b>4.2 Engagement relatif à l'offre gratuite.</b> Toute fonctionnalité accessible sans paiement à la date du {{ doc.effectiveDateFr }} le restera.
        Jiten+ ajoute des capacités ; il n'en retire aucune.
      </p>
      <p>Cet engagement est pris de bonne foi et lie Jitensoft, sous les seules réserves suivantes :</p>
      <ul>
        <li>
          a) nous pouvons modifier les modalités de mise à disposition, de présentation ou de mise en œuvre d'une fonctionnalité gratuite, dès lors que la
          capacité demeure accessible sans paiement ;
        </li>
        <li>
          b) nous pouvons instaurer ou ajuster des limites proportionnées à l'usage gratuit lorsque cela est nécessaire pour prévenir un abus, préserver la
          disponibilité du Service pour les autres Utilisateurs ou maîtriser un coût d'infrastructure disproportionné résultant d'un usage automatisé ou
          atypique ;
        </li>
        <li>
          c) nous pouvons retirer une fonctionnalité gratuite devenue illicite, ou dépendant d'un service ou d'un jeu de données tiers cessant de nous être
          accessible à des conditions viables, auquel cas nous l'annoncerons publiquement en en exposant les motifs ;
        </li>
        <li>d) cet engagement porte sur des fonctionnalités, et non sur un volume déterminé de stockage, de calcul ou de bande passante.</li>
      </ul>
      <p>Il n'emporte aucune obligation de rendre gratuites les fonctionnalités futures.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">5. Usages interdits</h2>
      <p>Vous vous engagez à ne pas :</p>
      <ul>
        <li>
          a) perturber, entraver, sonder ou tenter de contourner la sécurité, la limitation de débit, l'authentification ou les contrôles d'accès du
          Service ;
        </li>
        <li>
          b) accéder au Service par des moyens automatisés, sauf via l'API documentée et dans les limites de débit publiées, ou dans la mesure expressément
          autorisée par le fichier <code>robots.txt</code> ;
        </li>
        <li>
          c) extraire, copier ou reconstituer systématiquement tout ou partie substantielle du Corpus, que ce soit par aspiration de l'interface, itération
          sur l'API ou par tout autre moyen. Cette interdiction est fondée sur le droit sui generis du producteur de bases de données prévu à l'article
          L341-1 du Code de la propriété intellectuelle et est indépendante des licences accordées à l'article 6 ;
        </li>
        <li>
          d) revendre, sous-licencier, relayer, dupliquer le Service ou l'API, ou en fournir un accès payant, ni exploiter un service dont la fonction
          substantielle consiste à retransmettre les réponses de Jiten à des tiers ;
        </li>
        <li>e) créer des Comptes par des moyens automatisés, ni partager, vendre ou céder un Compte ou une clé d'API ;</li>
        <li>
          f) mettre en ligne un Contenu Utilisateur illicite, portant atteinte aux droits de tiers, ou que vous n'avez pas le droit de mettre en ligne ;
        </li>
        <li>g) utiliser le Service pour harceler autrui ou diffuser des logiciels malveillants.</li>
      </ul>
      <p>
        Nous pouvons suspendre ou résilier l'accès en cas de manquement au présent article, dans les conditions prévues à l'article 11.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">6. Droits sur les Données Dérivées et usages commerciaux autorisés</h2>
      <p>
        <b>6.1</b> Les Données Dérivées publiées par Jiten, notamment les decks d'étude, listes de vocabulaire et listes de fréquence, sont mises à
        disposition sous licence <a href="https://creativecommons.org/licenses/by-sa/4.0/deed.fr" target="_blank" rel="noopener">CC BY-SA 4.0</a>. Dans les
        limites de cette licence, <b>l'usage commercial est autorisé</b>, sous réserve d'attribution et de partage dans les mêmes conditions. Rien dans les
        présentes CGU ne restreint ce que vous pouvez faire de données obtenues licitement au titre de cette licence.
      </p>
      <p>
        <b>6.2</b> Les interdictions prévues à l'article 5 portent sur <i>l'accès au Service</i>, et non sur <i>l'usage des données licenciées</i>. Vous
        pouvez utiliser un deck téléchargé dans une formation payante. Vous ne pouvez pas revendre l'accès à Jiten lui-même, rendre payant un relais de
        l'API, ni extraire massivement le Corpus en vue de le reconstituer ailleurs.
      </p>
      <p>
        <b>6.3</b> L'interface, le code source, les signes distinctifs, les contenus éditoriaux originaux ainsi que la structure et le choix des éléments du
        Corpus demeurent la propriété de Jitensoft ou de ses concédants. Les composants open source sont régis par leurs licences respectives, publiées dans
        le dépôt du projet.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">7. Matériel de tiers et droit d'auteur</h2>
      <p>
        <b>7.1 Ce que nous analysons.</b> Pour produire les Données Dérivées, Jitensoft analyse du Matériel Source : textes issus de sous-titres d'animes,
        de romans, de visual novels, de jeux vidéo, de mangas et d'autres médias japonais comparables. Cette analyse consiste en des traitements de
        tokenisation, morphologiques et statistiques.
      </p>
      <p>
        <b>7.2 Fondement de l'analyse.</b> L'analyse de contenus licitement accessibles aux fins d'en extraire des informations statistiques constitue une
        fouille de textes et de données au sens de la directive (UE) 2019/790 et des articles L122-5 10° et L342-3 3° du Code de la propriété
        intellectuelle.
      </p>
      <p>
        <b>7.3 Le Matériel Source n'est pas diffusé.</b> Le Matériel Source est utilisé aux seules fins d'analyse. Il n'est ni publié, ni servi, ni rendu
        téléchargeable, ni reconstituable via l'interface, les fonctions d'export ou l'API. Aucune fonctionnalité du Service ne restitue le texte des œuvres
        sous-jacentes.
      </p>
      <p>
        <b>7.4 L'expression de tiers n'est jamais monétisée.</b> Lorsque le Service reproduit de courts extraits d'œuvres de tiers, principalement des
        phrases d'exemple illustrant l'emploi d'un mot, ceux-ci sont et demeureront accessibles <b>sans paiement</b>. Ils ne relèvent pas de l'offre Jiten+,
        ne sont conditionnés à aucune fonctionnalité payante, et aucun revenu n'est tiré de leur consultation.
      </p>
      <p>
        Jiten+ est vendu sur le fondement de traitements et d'un stockage personnels réalisés pour l'abonné à partir de ses propres données. Il ne confère
        aucun accès supplémentaire à l'expression de tiers, de quelque nature que ce soit. Jitensoft s'engage à ne pas placer de matériel textuel de tiers
        derrière l'offre payante.
      </p>
      <p>
        <b>7.5 Courte citation.</b> Les courts extraits visés à l'article 7.4 sont reproduits dans un but pédagogique et d'information, afin d'illustrer un
        usage, au sein d'un ensemble de nature analytique, et sont regardés comme relevant du droit de courte citation prévu à l'article L122-5, 3° a du
        Code de la propriété intellectuelle et à l'article 32 de la loi japonaise sur le droit d'auteur. Chacun est présenté avec l'indication de l'œuvre
        source. Tous les droits sur les œuvres citées demeurent ceux de leurs titulaires respectifs.
      </p>
      <p>
        <b>7.6 Traduction automatique.</b> Toute traduction automatique affichée est illustrative, peut être inexacte, ne constitue pas une citation et
        n'est couverte par aucune exception de citation.
      </p>
      <p>
        <b>7.7 Exactitude des analyses.</b> Les estimations de difficulté, pourcentages de couverture, décomptes et rangs de fréquence sont des estimations
        produites par traitement automatisé. Ils sont fournis de bonne foi et sans garantie d'exactitude. Ils ne constituent pas des affirmations de fait
        relatives à une œuvre.
      </p>
      <p>
        <b>7.8 Notification et retrait.</b> Jitensoft respecte les droits des titulaires. Si vous détenez des droits sur un contenu dont vous estimez
        l'usage illicite sur le Service, écrivez à <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> en indiquant :
      </p>
      <ul>
        <li>a) l'identification de l'œuvre concernée ;</li>
        <li>b) l'identification du contenu litigieux sur le Service, avec les URL ;</li>
        <li>c) vos nom, adresse postale et adresse électronique ;</li>
        <li>
          d) une déclaration selon laquelle vous détenez les droits concernés ou êtes habilité à agir, et estimez de bonne foi que l'usage n'est autorisé ni
          par le titulaire, ni par son mandataire, ni par la loi.
        </li>
      </ul>
      <p>
        Nous accuserons réception dans les meilleurs délais d'une notification complète, procéderons aux vérifications utiles et, en cas d'atteinte établie,
        retirerons le contenu concerné ou en rendrons l'accès impossible. Lorsque le contenu a été mis en ligne par un Utilisateur, nous l'en informerons et
        lui permettrons de présenter ses observations, sauf interdiction. Une notification abusive est susceptible d'engager la responsabilité de son auteur
        sur le fondement de l'article 6-I-4 de la LCEN.
      </p>
      <p>Jitensoft agit en qualité d'hébergeur au sens de l'article 6-I-2 de la LCEN s'agissant du Contenu Utilisateur.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">8. API et accès automatisé</h2>
      <p>
        <b>8.1</b> L'API est fournie dans les conditions des présentes CGU, de sa documentation publiée et de ses limites de débit publiées.
      </p>
      <p>
        <b>8.2</b> Les clés d'API sont personnelles, incessibles et révocables. Nous pouvons révoquer une clé, sans préavis lorsque cela est nécessaire, en
        cas de manquement à l'article 5, d'usage menaçant la stabilité du Service, ou lorsque la clé paraît compromise.
      </p>
      <p>
        <b>8.3</b> Les limites de débit, quotas et points d'accès disponibles peuvent évoluer. Nous informerons dans un délai raisonnable des changements
        non rétrocompatibles lorsque cela est possible.
      </p>
      <p>
        <b>8.4</b> L'attribution est requise lors de la publication de données obtenues via l'API, dans les conditions de la licence CC BY-SA 4.0.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">9. Contenu Utilisateur</h2>
      <p>
        <b>9.1</b> Vous restez propriétaire de votre Contenu Utilisateur, notamment des définitions personnalisées, phrases personnalisées, images et
        fichiers audio associés aux cartes, notations, notes et demandes de médias.
      </p>
      <p>
        <b>9.2</b> Vous concédez à Jitensoft une licence non exclusive, mondiale et gratuite d'héberger, stocker, reproduire, adapter à des fins techniques
        et afficher votre Contenu Utilisateur, dans la seule mesure nécessaire à l'exploitation du Service et à sa mise à disposition à votre profit et,
        lorsque vous avez choisi de le rendre public, au profit des autres Utilisateurs. Cette licence prend fin à la suppression du contenu, sous réserve
        des sauvegardes jusqu'à l'expiration de leur cycle de conservation.
      </p>
      <p>
        <b>9.3</b> Vous garantissez détenir les droits nécessaires sur le Contenu Utilisateur que vous mettez en ligne, et notamment le droit de téléverser
        toute image ou fichier audio associé à une carte d'étude. Vous en êtes seul responsable.
      </p>
      <p>
        <b>9.4</b> Nous pouvons retirer tout Contenu Utilisateur contrevenant aux présentes CGU, dans les conditions de l'article 7.8.
      </p>
      <p>
        <b>9.5</b> Vous pouvez exporter vos données personnelles, y compris l'état de votre vocabulaire et votre historique d'étude, à tout moment. L'export
        est disponible dans l'offre gratuite et n'est jamais conditionné à un paiement. L'export peut être soumis à des limites techniques proportionnées —
        telles que la taille des requêtes, leur fréquence ou le délai de traitement — afin de préserver la disponibilité du Service ; ces limites ne rendent
        jamais l'export indisponible, incomplet ou payant.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">10. Disponibilité, évolutions et garanties</h2>
      <p>
        <b>10.1</b> Le Service est fourni « en l'état » et « selon disponibilité ». Nous ne garantissons ni une disponibilité ininterrompue, ni l'absence
        d'erreurs, ni l'exactitude ou l'exhaustivité des Données Dérivées.
      </p>
      <p>
        <b>10.2</b> Nous pouvons modifier, suspendre pour maintenance ou interrompre des éléments du Service. Lorsqu'une interruption affecte
        significativement les Utilisateurs, nous en informerons publiquement dans un délai raisonnable. L'interruption affectant une offre payante est régie
        par les Conditions Générales de Vente.
      </p>
      <p>
        <b>10.3</b> Aucune stipulation du présent article n'exclut la garantie légale de conformité due aux consommateurs au titre des articles L224-25-12
        et suivants du Code de la consommation s'agissant de Jiten+, ni aucun autre droit du consommateur auquel il ne peut être licitement dérogé.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">11. Suspension et résiliation</h2>
      <p><b>11.1</b> Vous pouvez cesser d'utiliser le Service et supprimer votre Compte à tout moment.</p>
      <p>
        <b>11.2</b> Nous pouvons suspendre ou résilier l'accès en cas de manquement substantiel aux présentes CGU, lorsque la loi l'exige, ou lorsque votre
        usage menace la sécurité ou la disponibilité du Service.
      </p>
      <p>
        <b>11.3</b> Sauf manquement grave, illicite, ou persistant et dommageable, nous vous en indiquerons le motif et, lorsque le manquement est
        susceptible d'être régularisé, vous laisserons un délai raisonnable pour le faire.
      </p>
      <p>
        <b>11.4</b> Lorsque vous détenez un abonnement Jiten+ en cours, la résiliation pour manquement ne vous prive pas des droits à remboursement prévus
        par les Conditions Générales de Vente.
      </p>
      <p><b>11.5</b> Survivent à la résiliation les articles 6.3, 7, 12 et 14.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">12. Responsabilité</h2>
      <p>
        <b>12.1</b> Jitensoft répond des dommages causés par l'inexécution de ses obligations au titre des présentes CGU dans les conditions du droit
        français.
      </p>
      <p><b>12.2</b> Jitensoft n'est pas responsable :</p>
      <ul>
        <li>
          a) des préjudices résultant du recours aux estimations de difficulté, aux taux de couverture ou aux autres Données Dérivées, lesquelles
          constituent des estimations au sens de l'article 7.7 ;
        </li>
        <li>
          b) de la perte ou de l'altération du Contenu Utilisateur, sauf lorsqu'elle résulte d'un manquement de notre part à mettre en œuvre des mesures
          techniques raisonnables. Il vous appartient de conserver vos propres copies des éléments auxquels vous tenez, l'export étant fourni gratuitement
          au titre de l'article 9.5 ;
        </li>
        <li>
          c) des actes, omissions ou indisponibilités des services tiers dont dépend le Service, notamment le traitement des paiements, la diffusion de
          contenu, l'acheminement des courriels et l'hébergement ;
        </li>
        <li>d) des indisponibilités imputables à un cas de force majeure au sens de l'article 1218 du Code civil.</li>
      </ul>
      <p>
        <b>12.3</b> Dans la mesure où la responsabilité n'est pas exclue par la loi, et hors les cas de dommage corporel, de dol ou de faute lourde ou
        intentionnelle, la responsabilité globale de Jitensoft envers un Utilisateur au titre d'une période de douze mois est limitée au plus élevé des deux
        montants suivants : (i) les sommes versées par cet Utilisateur à Jitensoft au cours de cette période et (ii) cinquante euros (50 EUR).
      </p>
      <p>
        <b>12.4</b> Aucune stipulation du présent article ne limite une responsabilité qui ne peut être licitement limitée, notamment au regard des articles
        R212-1 et L221-15 du Code de la consommation.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">13. Modification des CGU</h2>
      <p>
        <b>13.1</b> Nous pouvons modifier les présentes CGU. La version et la date figurant en tête du présent document identifient la version en vigueur.
      </p>
      <p>
        <b>13.2</b> Pour les modifications affectant substantiellement vos droits ou obligations, nous vous en informerons par courriel ou par un avis
        visible dans le produit au moins trente (30) jours avant leur entrée en vigueur, sauf délai plus court imposé par la loi ou justifié par un risque
        de sécurité ou juridique.
      </p>
      <p>
        <b>13.3</b> Si vous n'acceptez pas une modification substantielle, vous pouvez cesser d'utiliser le Service et supprimer votre Compte avant son
        entrée en vigueur. Lorsque vous détenez un abonnement Jiten+ en cours, l'article 12.4 des Conditions Générales de Vente s'applique.
      </p>
      <p>
        <b>13.4</b> Pour les modifications non substantielles, la poursuite de l'utilisation après publication vaut acceptation.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">14. Droit applicable, litiges et médiation</h2>
      <p><b>14.1</b> Les présentes CGU sont régies par le droit français.</p>
      <p>
        <b>14.2</b> Lorsque vous êtes un consommateur résidant dans l'Union européenne, ce choix ne vous prive pas de la protection que vous assurent les
        dispositions impératives de la loi de votre pays de résidence.
      </p>
      <p>
        <b>14.3 Réclamations.</b> Adressez toute réclamation en premier lieu à <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>. Nous en accuserons
        réception dans un délai raisonnable.
      </p>
      <p>
        <b>14.4 Médiation.</b> À défaut de résolution, le consommateur peut saisir gratuitement le médiateur de la consommation désigné dans les
        <NuxtLink to="/mentions-legales">Mentions légales</NuxtLink>, dans un délai d'un an à compter de la réclamation écrite.
      </p>
      <p>
        <b>14.5 Juridictions.</b> À défaut de résolution amiable, les litiges relèvent des juridictions compétentes. Lorsque vous êtes un consommateur, vous
        pouvez saisir la juridiction de votre lieu de résidence ou celle du domicile du défendeur, sans que les présentes CGU y fassent obstacle.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">15. Stipulations diverses</h2>
      <p>
        <b>15.1</b> Si une stipulation est jugée inapplicable, le reste demeure en vigueur et la stipulation concernée est réputée modifiée dans la mesure
        minimale nécessaire à son applicabilité.
      </p>
      <p><b>15.2</b> L'absence d'exercice d'un droit ne vaut pas renonciation à ce droit.</p>
      <p>
        <b>15.3</b> Vous ne pouvez céder vos droits au titre des présentes CGU. Jitensoft peut céder les présentes CGU dans le cadre d'un transfert du
        Service, sous réserve de l'article 8.5 des Conditions Générales de Vente.
      </p>
      <p>
        <b>15.4</b> La version anglaise est la version de référence du Service. Elle est disponible à l'adresse
        <NuxtLink to="/terms">jiten.moe/terms</NuxtLink>. Pour les consommateurs résidant en France, la version française prévaut.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">16. Contact</h2>
      <p>Jitensoft, {{ legal.address }}<br ><a href="mailto:contact@jiten.moe">contact@jiten.moe</a></p>
      <p>
        L'identification complète de l'éditeur, y compris le nom du directeur de la publication, figure dans les
        <NuxtLink to="/mentions-legales">Mentions légales</NuxtLink>.
      </p>
    </section>
  </div>
</template>
