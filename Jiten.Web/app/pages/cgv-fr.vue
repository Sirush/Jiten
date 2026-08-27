<script setup lang="ts">
  const { legal, configured } = useLegalIdentity();
  const doc = LEGAL_DOCUMENTS.cgv;
  const prices = JITEN_PLUS_PRICES;

  useSeoMeta({ title: 'Conditions Générales de Vente (Jiten+)' });
  useRobotsRule({ noindex: true, follow: true });
</script>

<template>
  <div class="legal-doc mx-auto max-w-3xl px-6 py-10 leading-relaxed text-gray-800 dark:text-gray-200">
    <h1 class="mb-2 text-center text-3xl font-bold text-gray-900 dark:text-gray-100">Conditions Générales de Vente (Jiten+)</h1>
    <p class="mb-1 text-center text-gray-600 dark:text-gray-400">
      <b>Vendeur :</b>
      Jitensoft, entreprise individuelle (voir les
      <NuxtLink to="/mentions-legales">Mentions légales</NuxtLink>
      )
    </p>
    <p class="mb-1 text-center text-gray-600 dark:text-gray-400">
      <b>Version :</b>
      {{ doc.version }} —
      <b>Entrée en vigueur :</b>
      {{ doc.effectiveDateFr }}
    </p>
    <p class="mb-8 text-center italic text-gray-500 dark:text-gray-400">
      <NuxtLink to="/cgv">English version available here</NuxtLink>
      . Pour les consommateurs résidant en France, la version française prévaut.
    </p>

    <div
      v-if="!configured"
      class="mb-10 rounded-lg border border-amber-400 bg-amber-50 p-4 font-semibold text-amber-800 dark:border-amber-600 dark:bg-amber-950 dark:text-amber-200"
    >
      La configuration de l'identité légale est manquante.
    </div>

    <p>
      Les présentes Conditions Générales de Vente (les « CGV ») régissent l'achat de Jiten+, l'offre payante optionnelle du service Jiten. Elles complètent les
      <NuxtLink to="/terms-fr">Conditions Générales d'Utilisation</NuxtLink>
      (« CGU »), qui demeurent applicables. En cas de contradiction portant sur la vente, les présentes CGV prévalent.
    </p>
    <p>
      La version des CGV acceptée lors de votre commande régit votre achat. Les versions ultérieures ne s'appliquent aux reconductions que dans les conditions
      de l'article 12.
    </p>

    <section class="mb-10">
      <h2 class="section-title">1. Vendeur</h2>
      <p>
        {{ legal.publisherName }}, exerçant sous le nom commercial Jitensoft, entreprise individuelle immatriculée en France sous le SIREN
        {{ legal.siren }} (SIRET {{ legal.siret }}), siège social {{ legal.address }}, contact
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>
        .
      </p>
      <p>Jitensoft est le vendeur. Le paiement est traité par Stripe pour le compte de Jitensoft ; Stripe n'est pas le vendeur.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">2. Champ d'application et qualité de l'acheteur</h2>
      <p>
        <b>2.1</b>
        Les présentes CGV s'appliquent aux consommateurs comme aux acheteurs non-consommateurs. Les stipulations expressément applicables aux consommateurs ne
        s'appliquent que lorsque l'acheteur est une personne physique agissant à des fins n'entrant pas dans le cadre de son activité commerciale, industrielle,
        artisanale, libérale ou agricole, au sens de l'article liminaire du Code de la consommation.
      </p>
      <p>
        <b>2.2</b>
        Vous devez disposer d'un Compte Jiten et être âgé d'au moins 15 ans, ou disposer de l'autorisation d'un titulaire de l'autorité parentale. Un mineur
        doit disposer de cette autorisation avant tout achat.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">3. Objet de Jiten+</h2>
      <p>
        <b>3.1</b>
        Jiten+ est un abonnement donnant accès à des fonctionnalités personnelles supplémentaires venant s'ajouter au service Jiten gratuit. L'ensemble des
        fonctionnalités, des niveaux et des limites applicables est décrit sur la
        <NuxtLink to="/jiten-plus">page Jiten+</NuxtLink>
        , qui fait partie intégrante des présentes CGV.
      </p>
      <p>
        <b>3.2 Ce que Jiten+ ne comprend pas.</b>
        Jiten+ ne confère aucun accès supplémentaire à du matériel textuel de tiers. Les phrases d'exemple et toute autre reproduction d'expression de tiers
        demeurent accessibles sans paiement, conformément à l'engagement pris à l'article 7.4 des CGU. Jiten+ est vendu sur le fondement de traitements et d'un
        stockage réalisés pour vous, à partir de vos propres données.
      </p>
      <p>
        <b>3.3 Le service gratuit n'est pas affecté.</b>
        L'achat de Jiten+ ne supprime ni ne dégrade aucune capacité accessible sans paiement. L'engagement de l'article 4.2 des CGU s'applique pleinement.
      </p>
      <p>
        <b>3.4</b>
        Les fonctionnalités de Jiten+ peuvent être ajoutées, améliorées ou remplacées pendant la durée de votre abonnement. En cas de réduction substantielle
        des fonctionnalités du niveau souscrit, l'article 12.3 s'applique.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">4. Offres et prix</h2>
      <p>
        <b>4.1</b>
        Jiten+ est proposé selon les formules suivantes :
      </p>
      <table>
        <thead>
          <tr>
            <th>Formule</th>
            <th>Prix</th>
            <th>Facturation</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>Mensuelle</td>
            <td>{{ prices.monthlyEur }} EUR</td>
            <td>Récurrente, mensuelle, jusqu'à résiliation</td>
          </tr>
          <tr>
            <td>Annuelle</td>
            <td>{{ prices.yearlyEur }} EUR</td>
            <td>Récurrente, annuelle, jusqu'à résiliation</td>
          </tr>
          <tr>
            <td>À vie</td>
            <td>{{ prices.lifetimeEur }} EUR</td>
            <td>Paiement unique, lorsqu'elle est proposée (voir article 8)</td>
          </tr>
        </tbody>
      </table>
      <p>
        <b>4.2</b>
        Les prix affichés sur la page Jiten+ au moment de votre commande prévalent. Les prix sont exprimés en euros.
      </p>
      <p>
        <b>4.3 Le prix affiché est le montant total dû.</b>
        Le montant affiché avant confirmation du paiement constitue le montant total dû au titre de la formule, toutes taxes éventuellement applicables
        comprises. Aucune taxe, aucun frais ni aucune majoration n'est ajouté au moment du paiement ni ultérieurement. Si Jitensoft devient redevable de la taxe
        sur la valeur ajoutée au titre de votre achat, cette charge est supportée par Jitensoft sur le prix stipulé au présent article et n'augmente pas le
        montant que vous payez. Toute modification du prix lui-même est régie par l'article 12.
      </p>
      <p>
        <b>4.4 Situation fiscale actuelle.</b>
        Jitensoft bénéficie actuellement de la franchise en base de taxe sur la valeur ajoutée :
        <i>TVA non applicable, article 293 B du CGI</i>
        . Aucune TVA n'est donc facturée sur ces prix ni récupérable à ce titre, et cette mention figure sur chaque facture émise. Le présent article énonce un
        fait relatif à la situation fiscale actuelle de Jitensoft ; il ne constitue pas une stipulation contractuelle, et sa modification n'affecte pas le prix
        dû au titre de l'article 4.3.
      </p>
      <p>
        <b>4.5</b>
        Les frais bancaires, de conversion de devise ou prélevés par l'émetteur de votre moyen de paiement sont à votre charge.
      </p>
      <p>
        <b>4.6</b>
        La formule À vie n'est proposée que pendant des périodes annoncées. Elle peut être retirée à tout moment. Aucun droit à l'achat ne résulte d'une mise à
        disposition passée.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">5. Commande et formation du contrat</h2>
      <p>
        <b>5.1</b>
        Le processus de commande est le suivant : sélection d'une formule sur la page Jiten+, vérification de la formule, du prix et de la périodicité de
        facturation, acceptation des présentes CGV, recueil de la déclaration exigée à l'article 9 le cas échéant, puis paiement sur la page sécurisée hébergée
        par Stripe.
      </p>
      <p>
        <b>5.2</b>
        Avant paiement vous sont indiqués la formule, le montant total, la périodicité de facturation et les modalités de reconduction. Vous êtes en mesure
        d'identifier et de corriger d'éventuelles erreurs avant confirmation.
      </p>
      <p>
        <b>5.3</b>
        Le contrat est formé lors de la confirmation du paiement et de l'envoi de notre confirmation par courriel.
      </p>
      <p>
        <b>5.4</b>
        Nous adressons une confirmation à l'adresse électronique associée à votre Compte, sur support durable, mentionnant la formule, le montant payé, la
        périodicité de facturation, la date de reconduction ou la date de fin d'accès, la déclaration donnée au titre de l'article 9 et un lien vers les CGV
        acceptées.
      </p>
      <p>
        <b>5.5</b>
        Nous pouvons refuser une commande en cas de soupçon raisonnable de fraude, d'abus des crédits promotionnels ou de manquement aux CGU.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">6. Paiement</h2>
      <p>
        <b>6.1</b>
        Le paiement s'effectue par les moyens proposés lors de la commande, traités par Stripe Payments Europe, Ltd. Jitensoft ne reçoit ni ne conserve vos
        données de carte bancaire.
      </p>
      <p>
        <b>6.2</b>
        Pour les formules récurrentes, vous autorisez Jitensoft, par l'intermédiaire de Stripe, à débiter le moyen de paiement enregistré à chaque reconduction,
        au prix alors applicable dans les conditions de l'article 12.
      </p>
      <p>
        <b>6.3 Échec de paiement.</b>
        En cas d'échec d'un paiement de reconduction, Stripe procédera à de nouvelles tentatives pendant une période limitée. À défaut de paiement, l'accès à
        Jiten+ prend fin au terme de la période déjà réglée. Vos données sont conservées dans les conditions de l'article 10. Nous vous informerons avant la fin
        de l'accès.
      </p>
      <p>
        <b>6.4</b>
        Il vous appartient de maintenir un moyen de paiement valide et une adresse de facturation à jour.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">7. Durée, reconduction et avis de reconduction</h2>
      <p>
        <b>7.1</b>
        Les formules Mensuelle et Annuelle sont conclues pour la durée indiquée et se reconduisent automatiquement pour des périodes successives de même durée,
        jusqu'à résiliation.
      </p>
      <p>
        <b>7.2 Avis de reconduction.</b>
        Conformément à l'article L215-1 du Code de la consommation, pour toute formule reconduite tacitement pour une durée d'un an ou plus, nous vous
        informerons par écrit, par courriel, de la date limite à laquelle vous pouvez vous opposer à la reconduction. Cet avis sera adressé au plus tôt trois
        (3) mois et au plus tard un (1) mois avant le terme de la période autorisant le rejet de la reconduction.
      </p>
      <p>
        À défaut d'envoi de cet avis, vous pouvez mettre fin gratuitement au contrat à tout moment à compter de la date de reconduction, et les sommes versées
        après cette date vous seront remboursées dans un délai de trente (30) jours à compter de la résiliation, déduction faite des sommes correspondant à la
        période effectivement utilisée.
      </p>
      <p>
        <b>7.3</b>
        Vous pouvez résilier à tout moment. La résiliation prend effet au terme de la période en cours. Jiten+ demeure pleinement accessible jusqu'à cette date.
        La résiliation d'une formule récurrente n'ouvre pas droit au remboursement de la période en cours, sous réserve des articles 7.2, 9 et 12.3.
      </p>
      <p>
        <b>7.4 Modalités de résiliation.</b>
        Conformément à l'article L215-1-1 du Code de la consommation, la résiliation s'effectue en ligne, par un moyen au moins aussi simple que la
        souscription, depuis les
        <NuxtLink to="/settings/subscription">paramètres de votre abonnement</NuxtLink>
        . Aucun appel téléphonique, courrier ni contact avec le support n'est requis.
      </p>
      <p>
        <b>7.5</b>
        Une fois la résiliation effective, votre Compte revient à l'offre gratuite. L'article 10 régit le sort des données créées au moyen des fonctionnalités
        Jiten+.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">8. Accès à vie</h2>
      <p>
        <b>8.1 Portée.</b>
        L'accès à vie s'entend de la durée d'exploitation du service Jiten, et non de la durée de vie de l'acheteur. Il n'est pas perpétuel en ce qu'il ne
        survit pas à l'arrêt du Service.
      </p>
      <p>
        <b>8.2</b>
        L'accès à vie est attaché au Compte l'ayant acquis. Il est personnel et incessible, et ne peut être vendu, offert ni transféré vers un autre Compte.
      </p>
      <p>
        <b>8.3</b>
        L'accès à vie porte sur l'offre Jiten+ telle qu'elle évolue. Il n'oblige pas Jitensoft à créer une fonctionnalité future déterminée, ni à inclure dans
        Jiten+ un produit que Jitensoft pourrait ultérieurement proposer séparément. Un tel produit distinct sera véritablement additionnel et ne consistera pas
        en une capacité comprise dans Jiten+ à la date de l'achat.
      </p>
      <p>
        <b>8.4 En cas d'arrêt du Service.</b>
        Si Jitensoft met définitivement fin au service Jiten, nous nous engageons à :
      </p>
      <ul>
        <li>a) donner un préavis public d'au moins quatre-vingt-dix (90) jours avant la fin de l'accès ;</li>
        <li>b) maintenir l'export des données accessible et gratuit pendant toute cette période ;</li>
        <li>
          c) rembourser les titulaires d'un accès à vie
          <b>acquis à titre onéreux</b>
          selon le barème suivant, calculé de la date d'achat à la date de fin d'accès :
        </li>
      </ul>
      <table>
        <thead>
          <tr>
            <th>Arrêt du Service survenant</th>
            <th>Remboursement du prix effectivement payé</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>Dans les 0 à 12 mois de l'achat</td>
            <td>75 %</td>
          </tr>
          <tr>
            <td>Dans les 12 à 24 mois de l'achat</td>
            <td>50 %</td>
          </tr>
          <tr>
            <td>Dans les 24 à 36 mois de l'achat</td>
            <td>25 %</td>
          </tr>
          <tr>
            <td>Au-delà de 36 mois</td>
            <td>aucun</td>
          </tr>
        </tbody>
      </table>
      <p>
        Les points a) et b) s'appliquent à tout Compte, qu'un paiement ait été effectué ou non. Le point c) est calculé sur le prix effectivement payé au titre
        de la formule À vie et n'ouvre aucun droit lorsque l'accès a été octroyé à titre gratuit au titre de l'article 13.
      </p>
      <p>
        Les titulaires d'une formule récurrente ne seront pas débités au titre d'une période postérieure à la date de fin annoncée, et toute somme déjà versée
        au titre d'une période postérieure à cette date sera intégralement remboursée.
      </p>
      <p>
        Le présent article ne s'applique pas lorsque l'arrêt du Service résulte d'un cas de force majeure au sens de l'article 1218 du Code civil, auquel cas
        Jitensoft remboursera les sommes qu'il lui est raisonnablement possible de rembourser. Il ne limite aucun droit du consommateur auquel il ne peut être
        licitement dérogé.
      </p>
      <p>
        <b>8.5 Transfert du Service.</b>
        Si Jitensoft transfère le Service à un autre exploitant, celui-ci devra reprendre les présentes CGV, y compris les engagements des articles 4.2 et 7.4
        des CGU et de l'article 8.4 ci-dessus, à défaut de quoi le transfert sera traité comme un arrêt du Service au sens de l'article 8.4.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">9. Droit de rétractation</h2>
      <p>
        <b>9.1</b>
        En qualité de consommateur, vous disposez d'un droit de rétractation de quatorze (14) jours à compter de la conclusion du contrat.
      </p>
      <p>
        <b>9.2 Renonciation en cas d'accès immédiat.</b>
        Jiten+ est fourni immédiatement après paiement. Avant de finaliser le paiement, il vous est demandé de confirmer, et vous devez confirmer, une
        déclaration expresse aux termes de laquelle :
      </p>
      <ul>
        <li>
          a) vous
          <b>demandez que la fourniture de Jiten+ commence immédiatement</b>
          , avant l'expiration du délai de quatorze jours ; et
        </li>
        <li>
          b) vous
          <b>reconnaissez perdre, de ce fait, votre droit de rétractation</b>
          dès le commencement de la fourniture.
        </li>
      </ul>
      <p>
        Lorsque cette déclaration est donnée et que la fourniture a commencé, le droit de rétractation est éteint conformément à l'article L221-28 13° du Code
        de la consommation. Cette déclaration est enregistrée et reprise dans la confirmation adressée au titre de l'article 5.4.
      </p>
      <p>
        <b>9.3</b>
        À défaut de cette déclaration, l'achat ne peut être finalisé. Écrivez à
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>
        pour convenir d'un achat dont la fourniture est différée jusqu'à l'expiration du délai de rétractation.
      </p>
      <p>
        <b>9.4 Politique de remboursement volontaire.</b>
        Indépendamment de l'article 9.2, et sans préjudice de celui-ci, Jitensoft remboursera intégralement tout premier achat de Jiten+ annulé dans les
        quatorze (14) jours du paiement, sur demande adressée à
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>
        , dès lors que l'abonné n'a pas fait un usage substantiel des fonctionnalités payantes. Il s'agit d'un engagement commercial, et non du rappel d'un
        droit légal ; il ne s'applique ni aux reconductions ni aux achats répétés depuis un même Compte.
      </p>
      <p>
        <b>9.5</b>
        Les remboursements sont effectués par le moyen de paiement d'origine dans un délai de quatorze (14) jours à compter de l'acceptation de la demande.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">10. Sort de vos données en cas d'arrêt du paiement</h2>
      <p>
        <b>10.1</b>
        La fin d'un abonnement n'entraîne aucune suppression de vos données.
      </p>
      <p>
        <b>10.2</b>
        En cas d'expiration ou de résiliation :
      </p>
      <ul>
        <li>
          a) votre vocabulaire, votre historique d'étude, vos decks, vos statistiques et votre Compte demeurent intacts et accessibles dans l'offre gratuite ;
        </li>
        <li>
          b) les fichiers que vous avez mis en ligne au moyen des fonctionnalités Jiten+, notamment les images et fichiers audio associés aux cartes, sont
          <b>conservés et non supprimés</b>
          . Ils demeurent consultables, téléchargeables et supprimables par vous. La mise en ligne de nouveaux fichiers, ainsi que toute fonctionnalité
          d'actualisation ou de régénération de résultats payants, cesse ;
        </li>
        <li>
          c) les éléments produits au moyen de Jiten+, tels que les listes de fréquence enregistrées et les plans d'immersion, demeurent consultables et
          exportables. Leur mise à jour automatique cesse ;
        </li>
        <li>
          d) les avantages périodiques inclus dans Jiten+, actuels ou futurs, tels que des crédits mensuels de fonctionnalités ou des quotas d'utilisation,
          s'entendent par période : ils ne se cumulent pas, ne se reportent pas d'une période sur l'autre et cessent à la fin de l'abonnement. Les effets déjà
          produits par l'utilisation d'un tel avantage ne sont pas remis en cause ;
        </li>
        <li>e) l'export de vos données personnelles demeure accessible gratuitement, conformément à l'article 9.5 des CGU.</li>
      </ul>
      <p>
        <b>10.3</b>
        Nous ne supprimerons pas les fichiers conservés du fait d'un défaut de paiement. Nous pouvons les supprimer à votre demande, lors de la suppression de
        votre Compte, ou lorsque la loi l'exige. Si la conservation des fichiers devenait économiquement insoutenable à grande échelle, nous informerions les
        Utilisateurs concernés au moins quatre-vingt-dix (90) jours à l'avance, avec export gratuit, et n'appliquerions pas une telle modification de façon
        rétroactive aux fichiers mis en ligne avant cet avis.
      </p>
      <p>
        <b>10.4</b>
        Le réabonnement rétablit l'accès complet aux données conservées, sans perte.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">11. Garantie légale de conformité</h2>
      <p>
        <b>11.1</b>
        Jitensoft fournit Jiten+ conformément au contrat et répond des défauts de conformité existant lors de la fourniture et apparaissant pendant la durée de
        celle-ci, dans les conditions des articles L224-25-12 et suivants du Code de la consommation.
      </p>
      <p>
        <b>11.2</b>
        S'agissant d'un abonnement fourni de manière continue, cette responsabilité s'étend à toute la durée de la fourniture.
      </p>
      <p>
        <b>11.3</b>
        En cas de défaut de conformité établi, vous pouvez en exiger la mise en conformité. Si celle-ci est impossible, disproportionnée, ou n'intervient pas
        dans un délai raisonnable ou sans inconvénient majeur pour vous, vous pouvez obtenir une réduction du prix ou la résolution du contrat, dans les
        conditions prévues par le Code de la consommation.
      </p>
      <p>
        <b>11.4</b>
        Ces droits s'ajoutent au droit de rétractation prévu à l'article 9 et à tout geste commercial que nous pourrions consentir. Aucune stipulation des
        présentes CGV ne les limite.
      </p>
      <p>
        <b>11.5</b>
        Jitensoft est également tenu de la garantie des vices cachés prévue aux articles 1641 et suivants du Code civil lorsqu'elle est applicable.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">12. Modification des prix et des offres</h2>
      <p>
        <b>12.1</b>
        Nous pouvons modifier à tout moment le prix d'une formule pour les nouveaux achats.
      </p>
      <p>
        <b>12.2 Abonnés en cours.</b>
        Le prix d'un abonnement récurrent n'augmente pas tant que celui-ci demeure actif sans interruption : les augmentations de prix ne s'appliquent qu'aux
        nouvelles souscriptions, ainsi qu'aux abonnements souscrits de nouveau après une résiliation. Par exception, lorsqu'une évolution résulte exclusivement
        d'un changement des taxes applicables (par exemple si la TVA devient exigible sur nos ventes), le prix toutes taxes comprises peut évoluer en
        conséquence. Dans ce cas, nous vous en informerons par écrit au moins trente (30) jours avant la date de reconduction à laquelle elle prend effet, en
        indiquant le prix actuel, le nouveau prix et sa date d'effet, et en rappelant les modalités de résiliation. Si vous ne souhaitez pas l'accepter,
        résiliez avant cette date de reconduction ; le nouveau prix ne vous sera pas facturé.
      </p>
      <p>
        <b>12.3 Réduction substantielle des fonctionnalités.</b>
        En cas de réduction substantielle des fonctionnalités du niveau souscrit, nous vous en informerons au moins trente (30) jours à l'avance. Vous pourrez
        alors résilier avec effet à la date de la modification et obtenir le remboursement au prorata de la fraction non utilisée de la période en cours. Les
        titulaires d'un accès à vie concernés par une réduction substantielle pourront se prévaloir de l'article 8.4 comme si le Service avait cessé à la date
        de la modification, à moins qu'il n'y soit remédié dans un délai de quatre-vingt-dix (90) jours.
      </p>
      <p>
        <b>12.4</b>
        Les présentes CGV peuvent être modifiées pour les achats et reconductions à venir. La version applicable à une reconduction est celle en vigueur trente
        (30) jours avant la date de reconduction, les modifications substantielles étant notifiées dans les conditions de l'article 12.2.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">13. Crédits promotionnels, périodes d'essai et attributions à titre gratuit</h2>
      <p>
        <b>13.1</b>
        Les crédits promotionnels donnent accès à Jiten+ pour un nombre de jours indiqué. Ils sont gratuits, ne constituent pas un achat, n'ouvrent aucun droit
        à remboursement et peuvent être retirés lorsqu'ils ont été obtenus abusivement, notamment au moyen de plusieurs Comptes.
      </p>
      <p>
        <b>13.2</b>
        Les jours de crédit ne se décomptent que lorsqu'aucun abonnement payant n'est actif.
      </p>
      <p>
        <b>13.3</b>
        Les fonctionnalités incluses avec un crédit promotionnel sont celles indiquées au moment de son utilisation. Lorsqu'un crédit ouvre droit à un niveau
        restreint, cette restriction est indiquée lors de son utilisation et dans le courriel de confirmation.
      </p>
      <p>
        <b>13.4</b>
        Les crédits promotionnels ne se reconduisent pas et ne se transforment pas automatiquement en abonnement payant. Aucun moyen de paiement n'est requis
        pour les utiliser.
      </p>
      <p>
        <b>13.5 Attributions à titre gratuit.</b>
        Jitensoft peut accorder gratuitement et discrétionnairement un accès à Jiten+, pour une durée déterminée ou qualifié d'accès à vie. Une telle
        attribution est faite à titre gratuit : aucun prix n'est payé, elle ne constitue pas une vente, et les articles 4 à 9 et 12 ne lui sont pas applicables.
        Aucun moyen de paiement n'est conservé et aucun débit n'est jamais effectué.
      </p>
      <p>
        <b>13.6</b>
        Une attribution est personnelle au Compte, incessible, et n'ouvre aucun droit à remboursement, indemnisation ou prestation de substitution, y compris en
        cas d'arrêt du Service au titre de l'article 8.4. Les points a) et b) de l'article 8.4 s'appliquent néanmoins pleinement aux bénéficiaires.
      </p>
      <p>
        <b>13.7</b>
        Une attribution qualifiée d'accès à vie s'entend de la durée d'exploitation du Service, sur le même fondement que les articles 8.1 à 8.3. Jitensoft
        honorera une telle attribution aussi longtemps que le Service sera exploité et ne la retirera pas afin de convertir son bénéficiaire en abonné payant.
        Une attribution ne peut être retirée que si elle a été obtenue abusivement, ou en cas de résiliation du Compte pour manquement substantiel au titre de
        l'article 11 des CGU.
      </p>
      <p>
        <b>13.8</b>
        L'article 10 s'applique pleinement aux bénéficiaires d'une attribution. Rien de ce que vous avez mis en ligne ou généré n'est supprimé à l'expiration ou
        à l'épuisement d'une attribution, et l'export de vos données demeure gratuit.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">14. Suspension</h2>
      <p>
        Nous pouvons suspendre Jiten+ en cas d'échec de paiement dans les conditions de l'article 6.3, lorsque la loi l'exige, ou en cas de résiliation du
        Compte au titre de l'article 11 des CGU. La suspension prononcée au titre de l'article 11 des CGU pour manquement substantiel n'ouvre pas droit à
        remboursement ; toutefois, si nous résilions un Compte assorti d'un abonnement payant pour un motif qui ne vous est pas imputable, nous rembourserons au
        prorata la fraction non utilisée de la période en cours, et un titulaire d'un accès à vie pourra se prévaloir de l'article 8.4.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">15. Responsabilité</h2>
      <p>
        L'article 12 des CGU s'applique aux achats effectués au titre des présentes CGV, sous réserve que le plafond prévu à l'article 12.3 des CGU ne puisse,
        pour un acheteur de Jiten+, être inférieur au montant total versé par cet acheteur à Jitensoft.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">16. Données personnelles</h2>
      <p>
        Le traitement des données personnelles lié à un achat, y compris les données transmises à Stripe, est décrit dans la
        <NuxtLink to="/privacy">Politique de confidentialité</NuxtLink>
        . La base légale du traitement des données de facturation est l'exécution du présent contrat et le respect des obligations comptables.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">17. Réclamations, médiation et droit applicable</h2>
      <p>
        <b>17.1</b>
        Adressez toute réclamation à
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>
        .
      </p>
      <p>
        <b>17.2</b>
        À défaut de résolution, le consommateur peut saisir gratuitement le médiateur de la consommation désigné dans les
        <NuxtLink to="/mentions-legales">Mentions légales</NuxtLink>
        , dans un délai d'un an à compter de la réclamation écrite.
      </p>
      <p>
        <b>17.3</b>
        Les présentes CGV sont régies par le droit français. Lorsque vous êtes un consommateur résidant dans l'Union européenne, cela ne vous prive pas de la
        protection que vous assurent les dispositions impératives de la loi de votre pays de résidence, et vous pouvez saisir la juridiction de votre lieu de
        résidence.
      </p>
      <p>
        <b>17.4</b>
        La version anglaise, version de référence, est disponible à l'adresse
        <NuxtLink to="/cgv">jiten.moe/cgv</NuxtLink>
        . Pour les consommateurs résidant en France, la version française prévaut.
      </p>
    </section>
  </div>
</template>
