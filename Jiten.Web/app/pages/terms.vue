<script setup lang="ts">
  const { legal } = useLegalIdentity();
  const doc = LEGAL_DOCUMENTS.cgu;

  useSeoMeta({ title: 'Terms of Use (CGU)' });
</script>

<template>
  <div class="legal-doc mx-auto max-w-3xl px-6 py-10 leading-relaxed text-gray-800 dark:text-gray-200">
    <h1 class="mb-2 text-center text-3xl font-bold text-gray-900 dark:text-gray-100">Terms of Use / Conditions Générales d'Utilisation</h1>
    <p class="mb-1 text-center text-gray-600 dark:text-gray-400">
      <b>Publisher:</b> Jitensoft, entreprise individuelle. Full publisher identification is given in the
      <NuxtLink to="/mentions-legales">Legal notice</NuxtLink>.
    </p>
    <p class="mb-1 text-center text-gray-600 dark:text-gray-400"><b>Version:</b> {{ doc.version }} — <b>Effective:</b> {{ doc.effectiveDate }}</p>
    <p class="mb-8 text-center italic text-gray-500 dark:text-gray-400">
      <NuxtLink to="/terms-fr">Version française disponible ici</NuxtLink>. For consumers resident in France, the French version prevails.
    </p>

    <p>
      These Terms of Use (the "Terms") govern access to and use of the website at <code>https://jiten.moe</code>, its sub-domains, its API, and the browser
      extension and companion tools published by Jitensoft (together, the "Service").
    </p>
    <p>
      Purchases of the paid Jiten+ tier are governed additionally by the <NuxtLink to="/cgv">Terms of Sale (CGV)</NuxtLink>. Where the two conflict on a
      matter of sale, the Terms of Sale prevail.
    </p>
    <p>By accessing or using the Service you agree to these Terms. If you do not agree, do not use the Service.</p>

    <section class="mb-10">
      <h2 class="section-title">1. Definitions</h2>
      <ul>
        <li><b>Jitensoft</b>, <b>we</b>, <b>us</b>: the publisher identified in the Legal notice.</li>
        <li><b>Service</b>: as defined above.</li>
        <li><b>User</b>, <b>you</b>: any person accessing the Service, whether or not registered.</li>
        <li><b>Account</b>: a registered user account on the Service.</li>
        <li><b>Jiten+</b>: the optional paid tier described in the Terms of Sale.</li>
        <li><b>Corpus</b>: the database of Japanese media, vocabulary, statistics and derived data compiled by Jitensoft.</li>
        <li><b>Source Material</b>: third-party Japanese textual material ingested for analysis.</li>
        <li>
          <b>Derived Data</b>: statistical and analytical output produced from Source Material, including word and character counts, frequency data,
          difficulty estimates, vocabulary lists and generated study decks.
        </li>
        <li><b>User Content</b>: material a User submits, uploads or creates within the Service.</li>
      </ul>
    </section>

    <section class="mb-10">
      <h2 class="section-title">2. The Service</h2>
      <p>
        Jiten analyses Japanese media and publishes statistics, vocabulary lists, frequency data, difficulty estimates and study material derived from that
        analysis. It also provides learning tools including a spaced repetition system, coverage tracking against a User's own vocabulary, deck export, a
        browser extension and companion applications.
      </p>
      <p>The Service is a study aid. It is not a course, a certification, a translation service, or a substitute for qualified instruction.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">3. Eligibility and Accounts</h2>
      <p>
        <b>3.1</b> You must be at least 15 years old (the age of digital consent in France) or have the consent of a holder of parental responsibility.
      </p>
      <p>
        <b>3.2</b> Much of the Service is usable without an Account. An Account is required for features that depend on knowing your personal vocabulary, and
        for Jiten+.
      </p>
      <p>
        <b>3.3</b> You are responsible for the confidentiality of your credentials and for activity under your Account. Notify us at
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> if you believe your Account has been compromised.
      </p>
      <p>
        <b>3.4</b> One natural person may not maintain multiple Accounts for the purpose of circumventing usage limits, promotional credits, trial
        entitlements or request-voting mechanisms.
      </p>
      <p>
        <b>3.5</b> You may delete your Account at any time from your account settings. Deletion is described in the
        <NuxtLink to="/privacy">Privacy Policy</NuxtLink>.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">4. Free access</h2>
      <p><b>4.1</b> The Service is free to use except for the optional Jiten+ tier.</p>
      <p>
        <b>4.2 Free-tier undertaking.</b> Every feature that is available without payment as at {{ doc.effectiveDate }} will remain available without
        payment. Jiten+ adds capabilities; it does not remove existing ones.
      </p>
      <p>This undertaking is given in good faith and is binding on Jitensoft, subject only to the following:</p>
      <ul>
        <li>a) we may change how a free feature is delivered, presented or implemented, provided the capability remains available without payment;</li>
        <li>
          b) we may impose or adjust proportionate limits on free usage where necessary to prevent abuse, to protect the availability of the Service for
          other Users, or to control disproportionate infrastructure cost caused by automated or atypical use;
        </li>
        <li>
          c) we may withdraw a free feature that becomes unlawful, or that depends on a third-party service or dataset that ceases to be available to us on
          viable terms, in which case we will say so publicly and explain why;
        </li>
        <li>d) this undertaking applies to features, not to any particular quantity of storage, computation or bandwidth.</li>
      </ul>
      <p>It does not oblige us to make future features free.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">5. Acceptable use</h2>
      <p>You agree not to:</p>
      <ul>
        <li>
          a) interfere with, disrupt, probe, or attempt to circumvent the security, rate limiting, authentication or access controls of the Service;
        </li>
        <li>
          b) access the Service by automated means except through the documented API and within its published rate limits, or as expressly permitted by
          <code>robots.txt</code>;
        </li>
        <li>
          c) systematically extract, copy or reconstruct all or a substantial part of the Corpus, whether by scraping the interface, iterating the API, or
          otherwise. This restriction is asserted under the sui generis database right at article L341-1 of the French Intellectual Property Code and is
          independent of the licences granted in article 6;
        </li>
        <li>
          d) resell, sublicense, proxy, mirror, or provide paid access to the Service or the API, or operate a service whose substantial function is to
          relay Jiten's responses to third parties;
        </li>
        <li>e) create Accounts by automated means, or share, sell or transfer an Account or API key;</li>
        <li>f) upload User Content that is unlawful, that infringes the rights of others, or that you do not have the right to upload;</li>
        <li>g) use the Service to harass any person or to distribute malware.</li>
      </ul>
      <p>We may suspend or terminate access for breach of this article, in accordance with article 11.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">6. Rights in Derived Data, and what you may do commercially</h2>
      <p>
        <b>6.1</b> Derived Data published by Jiten, including study decks, vocabulary lists and frequency lists, is licensed under
        <a href="https://creativecommons.org/licenses/by-sa/4.0/" target="_blank" rel="noopener">CC BY-SA 4.0</a>. Within that licence, <b>commercial use is
        permitted</b>, subject to attribution and share-alike. Nothing in these Terms restricts what you may do with data you have lawfully obtained under
        that licence.
      </p>
      <p>
        <b>6.2</b> The restrictions in article 5 apply to <i>access to the Service</i>, not to <i>use of licensed data</i>. You may use a downloaded deck in
        a paid course. You may not resell access to Jiten itself, paywall a proxy of the API, or bulk-extract the Corpus in order to reconstitute it
        elsewhere.
      </p>
      <p>
        <b>6.3</b> The interface, source code, branding, original editorial content and the structure and selection of the Corpus remain the property of
        Jitensoft or its licensors. Open-source components are governed by their own licences, published in the project repository.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">7. Third-party material and copyright</h2>
      <p>
        <b>7.1 What we ingest.</b> To produce Derived Data, Jitensoft analyses Source Material: text from anime subtitles, novels, visual novels, games,
        manga and comparable Japanese media. Analysis consists of tokenisation, morphological and statistical processing.
      </p>
      <p>
        <b>7.2 Legal basis for analysis.</b> The analysis of lawfully accessible material for the purpose of extracting statistical information constitutes
        text and data mining within the meaning of Directive (EU) 2019/790 and articles L122-5 10° and L342-3 3° of the French Intellectual Property Code.
      </p>
      <p>
        <b>7.3 Source Material is not distributed.</b> Source Material is used for analysis only. It is not published, served, made downloadable, or made
        reconstructable through the interface, the export functions or the API. No feature of the Service returns the underlying text of a work.
      </p>
      <p>
        <b>7.4 Third-party expression is never monetised.</b> Where the Service reproduces short passages of third-party material, principally example
        sentences illustrating a word in use, that material is and will remain accessible <b>without payment</b>. It is not part of the Jiten+ tier, is not
        gated behind any paid feature, and no revenue is derived from access to it.
      </p>
      <p>
        Jiten+ is sold on the basis of personal computation and storage performed for the subscriber over the subscriber's own data. It confers no
        additional access to third-party expression of any kind. Jitensoft undertakes not to place third-party textual material behind the paid tier.
      </p>
      <p>
        <b>7.5 Quotation.</b> The short passages described in article 7.4 are reproduced for the pedagogical and informational purpose of demonstrating
        usage, in a work whose substance is analytical, and are believed to fall within the right of quotation at article L122-5, 3° a of the French
        Intellectual Property Code and article 32 of the Japanese Copyright Act. Each is presented with the source work identified. All rights in the quoted
        works remain with their respective holders.
      </p>
      <p>
        <b>7.6 Machine translation.</b> Any automatic translation shown is illustrative, may be inaccurate, is not a citation, and is not covered by any
        quotation exception.
      </p>
      <p>
        <b>7.7 Accuracy of analysis.</b> Difficulty estimates, coverage percentages, counts and frequency ranks are computed estimates produced by automated
        processing. They are provided in good faith and without warranty of accuracy. They are not statements of fact about a work.
      </p>
      <p>
        <b>7.8 Notice and takedown.</b> Jitensoft respects the rights of copyright holders. If you hold rights in material you believe is used unlawfully on
        the Service, write to <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> with:
      </p>
      <ul>
        <li>a) identification of the work concerned;</li>
        <li>b) identification of the material on the Service you consider infringing, with URLs;</li>
        <li>c) your name, postal address and email address;</li>
        <li>
          d) a statement that you hold the rights concerned or are authorised to act, and that you believe in good faith that the use is not authorised by
          the rightsholder, its agent, or the law.
        </li>
      </ul>
      <p>
        We will acknowledge a complete notice promptly, investigate, and where an infringement is established remove or disable the material concerned.
        Where the material was submitted by a User, we will notify that User and allow them to respond, unless prohibited from doing so. Deliberately false
        notices may engage the liability of their author under article 6-I-4 of the LCEN.
      </p>
      <p>Jitensoft acts as a hosting provider within the meaning of article 6-I-2 of the LCEN in respect of User Content.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">8. API and automated access</h2>
      <p><b>8.1</b> The API is provided under these Terms, its published documentation and its published rate limits.</p>
      <p>
        <b>8.2</b> API keys are personal, non-transferable and revocable. We may revoke a key, without notice where necessary, for breach of article 5, for
        use that threatens the stability of the Service, or where the key appears compromised.
      </p>
      <p>
        <b>8.3</b> Rate limits, quotas and endpoint availability may change. We will give reasonable notice of breaking changes where practicable.
      </p>
      <p><b>8.4</b> Attribution is required when publishing data obtained through the API, on the terms of the CC BY-SA 4.0 licence.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">9. User Content</h2>
      <p>
        <b>9.1</b> You retain ownership of your User Content, including custom meanings, custom sentences, uploaded card images and audio, ratings, notes
        and media requests.
      </p>
      <p>
        <b>9.2</b> You grant Jitensoft a non-exclusive, worldwide, royalty-free licence to host, store, reproduce, adapt for technical purposes and display
        your User Content, solely to the extent necessary to operate the Service and to make it available to you and, where you have chosen to make it
        public, to other Users. This licence ends when the content is deleted, save for backups pending expiry of their retention cycle.
      </p>
      <p>
        <b>9.3</b> You warrant that you hold the necessary rights in User Content you upload, and in particular that you have the right to upload any image
        or audio you attach to a study card. You are solely responsible for that material.
      </p>
      <p><b>9.4</b> We may remove User Content that breaches these Terms, in accordance with article 7.8.</p>
      <p>
        <b>9.5</b> You may export your personal data, including your vocabulary state and study history, at any time. Export is available on the free tier
        and is never gated behind payment. Export may be subject to proportionate technical limits — such as request size, frequency or processing time —
        to protect the availability of the Service; such limits never make export unavailable, incomplete or paid.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">10. Availability, changes and warranties</h2>
      <p>
        <b>10.1</b> The Service is provided "as is" and "as available". We do not warrant that it will be uninterrupted, error-free, or that Derived Data
        will be accurate or complete.
      </p>
      <p>
        <b>10.2</b> We may modify, suspend for maintenance, or discontinue parts of the Service. Where a discontinuation materially affects Users we will
        give reasonable public notice. Discontinuation affecting a paid tier is governed by the Terms of Sale.
      </p>
      <p>
        <b>10.3</b> Nothing in this article excludes the legal guarantee of conformity owed to consumers under articles L224-25-12 et seq. of the French
        Consumer Code in respect of Jiten+, or any other right a consumer has that cannot lawfully be excluded.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">11. Suspension and termination</h2>
      <p><b>11.1</b> You may stop using the Service and delete your Account at any time.</p>
      <p>
        <b>11.2</b> We may suspend or terminate access where you materially breach these Terms, where required by law, or where your use threatens the
        security or availability of the Service.
      </p>
      <p>
        <b>11.3</b> Except where the breach is serious, unlawful, or ongoing and harmful, we will notify you of the reason and, where the breach is capable
        of remedy, allow a reasonable opportunity to remedy it.
      </p>
      <p>
        <b>11.4</b> Where you hold an active Jiten+ subscription, termination for breach does not deprive you of the refund rights set out in the Terms of
        Sale.
      </p>
      <p><b>11.5</b> On termination, articles 6.3, 7, 12 and 14 survive.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">12. Liability</h2>
      <p>
        <b>12.1</b> Jitensoft is liable for damage caused by a failure to perform its obligations under these Terms in accordance with French law.
      </p>
      <p><b>12.2</b> Jitensoft is not liable for:</p>
      <ul>
        <li>
          a) loss arising from reliance on difficulty estimates, coverage figures or other Derived Data, which are estimates as stated in article 7.7;
        </li>
        <li>
          b) loss of, or corruption of, User Content, except where caused by our failure to take reasonable technical measures. You are responsible for
          retaining your own copies of material you value. Export is provided free under article 9.5;
        </li>
        <li>
          c) the acts, omissions or availability of third-party services on which the Service depends, including payment processing, content delivery, email
          delivery and hosting;
        </li>
        <li>d) unavailability attributable to force majeure within the meaning of article 1218 of the French Civil Code.</li>
      </ul>
      <p>
        <b>12.3</b> Where liability is not excluded by law, and other than for death or personal injury, fraud, or gross or wilful misconduct, Jitensoft's
        aggregate liability to a User in respect of any twelve-month period is limited to the greater of (i) the amounts paid by that User to Jitensoft in
        that period and (ii) fifty euros (50 EUR).
      </p>
      <p>
        <b>12.4</b> Nothing in this article limits liability that cannot lawfully be limited, including under articles R212-1 and L221-15 of the French
        Consumer Code.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">13. Changes to these Terms</h2>
      <p><b>13.1</b> We may amend these Terms. The version and date at the head of this document identify the current version.</p>
      <p>
        <b>13.2</b> For changes that materially affect your rights or obligations, we will give notice by email or by a prominent in-product notice at least
        thirty (30) days before the change takes effect, except where a shorter period is required by law or to address a security or legal risk.
      </p>
      <p>
        <b>13.3</b> If you do not accept a material change, you may stop using the Service and delete your Account before the change takes effect. Where you
        hold an active Jiten+ subscription, article 12.4 of the Terms of Sale applies instead.
      </p>
      <p><b>13.4</b> For non-material changes, continued use after publication constitutes acceptance.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">14. Governing law, disputes and mediation</h2>
      <p><b>14.1</b> These Terms are governed by French law.</p>
      <p>
        <b>14.2</b> Where you are a consumer resident in the European Union, this choice of law does not deprive you of the protection of mandatory
        provisions of the law of your country of residence.
      </p>
      <p>
        <b>14.3 Complaints.</b> Address any complaint first to <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>. We will acknowledge within a
        reasonable period.
      </p>
      <p>
        <b>14.4 Mediation.</b> If a complaint is not resolved, a consumer may refer the dispute free of charge to the consumer mediator designated in the
        <NuxtLink to="/mentions-legales">Legal notice</NuxtLink>, within one year of the written complaint.
      </p>
      <p>
        <b>14.5 Courts.</b> Failing amicable resolution, disputes are subject to the competent courts. Where you are a consumer, you may bring proceedings
        in the courts of your place of residence or of the defendant's domicile, and nothing in these Terms restricts that right.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">15. Miscellaneous</h2>
      <p>
        <b>15.1</b> If a provision is held unenforceable, the remainder continues in effect and the provision is to be read, so far as possible, as modified
        to the minimum extent necessary to make it enforceable.
      </p>
      <p><b>15.2</b> Failure to enforce a provision is not a waiver of it.</p>
      <p>
        <b>15.3</b> You may not assign your rights under these Terms. Jitensoft may assign these Terms in connection with a transfer of the Service, subject
        to article 8.5 of the Terms of Sale.
      </p>
      <p>
        <b>15.4</b> The English version is the operative version of the Service. A French version is available at
        <NuxtLink to="/terms-fr">jiten.moe/terms-fr</NuxtLink>. For consumers resident in France, the French version prevails.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">16. Contact</h2>
      <p>Jitensoft, {{ legal.address }}<br ><a href="mailto:contact@jiten.moe">contact@jiten.moe</a></p>
      <p>
        Full publisher identification, including the name of the publication director, is given in the
        <NuxtLink to="/mentions-legales">Legal notice</NuxtLink>.
      </p>
    </section>
  </div>
</template>
