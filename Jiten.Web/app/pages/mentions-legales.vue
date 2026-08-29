<script setup lang="ts">
  const { legal, configured } = useLegalIdentity();

  useSeoMeta({ title: 'Legal notice / Mentions légales' });
  useRobotsRule({ noindex: true, follow: true });
</script>

<template>
  <div class="legal-doc mx-auto max-w-3xl px-6 py-10 leading-relaxed text-gray-800 dark:text-gray-200">
    <h1 class="mb-2 text-center text-3xl font-bold text-gray-900 dark:text-gray-100">Legal notice / Mentions légales</h1>
    <p class="mb-10 text-center italic text-gray-500 dark:text-gray-400">Last updated / Dernière mise à jour : 8 August 2026</p>

    <div
      v-if="!configured"
      class="mb-10 rounded-lg border border-amber-400 bg-amber-50 p-4 font-semibold text-amber-800 dark:border-amber-600 dark:bg-amber-950 dark:text-amber-200"
    >
      Legal identity configuration is missing. / La configuration de l'identité légale est manquante.
    </div>

    <!-- English -->
    <section class="mb-10">
      <h2 class="section-title">Publisher / Éditeur du site</h2>
      <p>The site accessible at <code>https://jiten.moe</code> (the "Site") is published by:</p>
      <ul class="detail-list">
        <li><b>Legal name:</b> {{ legal.publisherName }} (entrepreneur individuel)</li>
        <li><b>Trade name:</b> Jitensoft</li>
        <li><b>Legal form:</b> Entreprise individuelle (microentreprise), registered in France</li>
        <li><b>Registered address:</b> {{ legal.address }}</li>
        <li><b>SIREN:</b> {{ legal.siren }}</li>
        <li><b>SIRET:</b> {{ legal.siret }}</li>
        <li><b>VAT:</b> TVA non applicable, article 293 B du CGI</li>
        <li><b>Contact:</b> <a href="mailto:contact@jiten.moe">contact@jiten.moe</a></li>
        <li><b>Publication director / Directeur de la publication:</b> {{ legal.publicationDirector }}</li>
      </ul>
    </section>

    <section class="mb-10">
      <h2 class="section-title">Hosting / Hébergeur</h2>
      <p>The Site is hosted by:</p>
      <ul class="detail-list">
        <li><b>Name:</b> Hetzner Online GmbH</li>
        <li><b>Address:</b> Industriestr. 25, 91710 Gunzenhausen, Germany</li>
        <li><b>Telephone:</b> +49 9831 505-0</li>
      </ul>
      <p>Content delivery is additionally performed by Bunny.net (BunnyWay d.o.o., Cesta Komandanta Staneta 4A, 1215 Medvode, Slovenia).</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">Consumer mediation / Médiation de la consommation</h2>
      <p>
        In accordance with articles L612-1 et seq. of the French Consumer Code, Jitensoft has designated the following consumer mediator, whose services are
        free of charge to the consumer:
      </p>
      <ul class="detail-list">
        <li><b>Name:</b> Société Médiation Professionnelle (SMP)</li>
        <li><b>Address:</b> 24 rue Albert de Mun, 33000 Bordeaux, France</li>
        <li>
          <b>Website:</b>
          <a href="https://www.mediateur-consommation-smp.fr" target="_blank" rel="noopener">https://www.mediateur-consommation-smp.fr</a>
        </li>
      </ul>
      <p>
        A consumer may refer a dispute to the mediator only after having first submitted a written complaint to
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> and within one year of that complaint.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">Intellectual property</h2>
      <p>
        The structure of the Site, its interface, its original editorial content, its software, and the database of media statistics compiled by Jitensoft are
        protected under French and international law, including the sui generis database right provided by article L341-1 of the French Intellectual Property
        Code.
      </p>
      <p>
        Certain datasets published by Jiten are made available under open licences. See the
        <NuxtLink to="/guides/deck-licensing">deck licensing guide</NuxtLink> and article 6 of the <NuxtLink to="/terms">Terms of Use</NuxtLink>.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">Reporting illegal content</h2>
      <p>
        Jitensoft acts as a hosting provider within the meaning of article 6-I-2 of the LCEN in respect of content uploaded by users. To report unlawful
        content, or to submit a copyright notice, write to <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> following the procedure in article 7.8 of
        the <NuxtLink to="/terms">Terms of Use</NuxtLink>.
      </p>
    </section>

    <hr class="my-10 border-gray-300 dark:border-gray-700" />

    <!-- Français -->
    <section class="mb-10">
      <h2 class="section-title">Éditeur du site</h2>
      <p>Le site accessible à l'adresse <code>https://jiten.moe</code> (le « Site ») est édité par :</p>
      <ul class="detail-list">
        <li><b>Dénomination légale :</b> {{ legal.publisherName }} (entrepreneur individuel)</li>
        <li><b>Nom commercial :</b> Jitensoft</li>
        <li><b>Forme juridique :</b> Entreprise individuelle (microentreprise), immatriculée en France</li>
        <li><b>Adresse du siège :</b> {{ legal.address }}</li>
        <li><b>SIREN :</b> {{ legal.siren }}</li>
        <li><b>SIRET :</b> {{ legal.siret }}</li>
        <li><b>TVA :</b> TVA non applicable, article 293 B du CGI</li>
        <li><b>Contact :</b> <a href="mailto:contact@jiten.moe">contact@jiten.moe</a></li>
        <li><b>Directeur de la publication :</b> {{ legal.publicationDirector }}</li>
      </ul>
    </section>

    <section class="mb-10">
      <h2 class="section-title">Hébergeur</h2>
      <p>Le Site est hébergé par :</p>
      <ul class="detail-list">
        <li><b>Dénomination :</b> Hetzner Online GmbH</li>
        <li><b>Adresse :</b> Industriestr. 25, 91710 Gunzenhausen, Allemagne</li>
        <li><b>Téléphone :</b> +49 9831 505-0</li>
      </ul>
      <p>La diffusion de contenu est en outre assurée par Bunny.net (BunnyWay d.o.o., Cesta Komandanta Staneta 4A, 1215 Medvode, Slovénie).</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">Médiation de la consommation</h2>
      <p>
        Conformément aux articles L612-1 et suivants du Code de la consommation, Jitensoft a désigné le médiateur de la consommation suivant, dont le recours
        est gratuit pour le consommateur :
      </p>
      <ul class="detail-list">
        <li><b>Dénomination :</b> Société Médiation Professionnelle (SMP)</li>
        <li><b>Adresse :</b> 24 rue Albert de Mun, 33000 Bordeaux, France</li>
        <li>
          <b>Site internet :</b>
          <a href="https://www.mediateur-consommation-smp.fr" target="_blank" rel="noopener">https://www.mediateur-consommation-smp.fr</a>
        </li>
      </ul>
      <p>
        Le consommateur ne peut saisir le médiateur qu'après avoir adressé une réclamation écrite à
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>, et dans un délai d'un an à compter de cette réclamation.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">Propriété intellectuelle</h2>
      <p>
        La structure du Site, son interface, ses contenus éditoriaux originaux, ses logiciels ainsi que la base de données de statistiques relatives aux médias
        constituée par Jitensoft sont protégés par le droit français et international, y compris par le droit sui generis du producteur de bases de données
        prévu à l'article L341-1 du Code de la propriété intellectuelle.
      </p>
      <p>
        Certains jeux de données publiés par Jiten sont mis à disposition sous licence ouverte. Voir le
        <NuxtLink to="/guides/deck-licensing">guide relatif aux licences des decks</NuxtLink> et l'article 6 des
        <NuxtLink to="/terms-fr">Conditions Générales d'Utilisation</NuxtLink>.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">Signalement de contenus illicites</h2>
      <p>
        Jitensoft agit en qualité d'hébergeur au sens de l'article 6-I-2 de la LCEN s'agissant des contenus mis en ligne par les utilisateurs. Pour signaler un
        contenu illicite ou notifier une atteinte au droit d'auteur, écrire à <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> en suivant la procédure
        prévue à l'article 7.8 des <NuxtLink to="/terms-fr">Conditions Générales d'Utilisation</NuxtLink>.
      </p>
    </section>
  </div>
</template>
