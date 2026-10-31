<script setup lang="ts">
  import type { ProfileBar, ProfileColumn, ProfileCount, ProfileStats, UserAccomplishment } from '~/types';
  import { getCompletedDisplay, getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { difficultyNames, EXPERT_BAND, shelfDifficultyBand } from '~/utils/difficultyColours';
  import { formatApprox, formatCompact } from '~/utils/siteStats';
  import { formatMonthYear, MONTHS_SHORT } from '~/utils/dateFormat';
  import { genreRows } from '~/utils/profileGenres';

  const { handle, header, isOwner, viewAsQuery, apiPath } = useProfileContext();
  const { chartColour } = useDifficultyColours();

  const [{ data: stats, status, error }, { data: accomplishments }] = await Promise.all([
    useApiFetch<ProfileStats>(() => apiPath('stats'), { query: viewAsQuery.value }),
    useApiFetch<UserAccomplishment[]>(() => apiPath('accomplishments'), { query: viewAsQuery.value }),
  ]);

  const tiles = computed(() => {
    const s = stats.value;
    if (!s) return [];
    const result: { label: string; value: string }[] = [];
    if (s.titlesFinished != null) result.push({ label: 'Titles finished', value: s.titlesFinished.toLocaleString('en-US') });
    if (s.characters != null) result.push({ label: 'Characters', value: formatCompact(s.characters) });
    if (s.meanScore != null) result.push({ label: `Mean score (${s.scoredCount} scored)`, value: s.meanScore.toFixed(1) });
    return result;
  });

  const monthRange = ref<'year' | 'all'>('year');
  const monthRangeOptions = [
    { label: '12 months', value: 'year' },
    { label: 'All time', value: 'all' },
  ];
  /** Width that fits a label such as "Jan 26" and a value such as "99.4K" on every column, so none are thinned out. */
  const MONTH_COLUMN_WIDTH = 40;

  const charsPerMonth = computed<ProfileColumn[]>(() => {
    const months = stats.value?.charactersByMonth ?? [];
    if (!months.length) return [];
    const byMonth = new Map(months.map((m) => [m.month, m.characters]));
    const now = new Date();
    const [firstYear, firstMonth] =
      monthRange.value === 'all'
        ? (months[0]!.month.split('-').map(Number) as [number, number])
        : [now.getMonth() >= 11 ? now.getFullYear() : now.getFullYear() - 1, ((now.getMonth() + 1) % 12) + 1];
    const total = (now.getFullYear() - firstYear) * 12 + now.getMonth() + 1 - firstMonth + 1;
    return Array.from({ length: total }, (_, i) => {
      const y = firstYear + Math.floor((firstMonth - 1 + i) / 12);
      const m = ((firstMonth - 1 + i) % 12) + 1;
      const key = `${y}-${String(m).padStart(2, '0')}`;
      const chars = byMonth.get(key) ?? 0;
      return {
        key,
        label: m === 1 || i === 0 ? `${MONTHS_SHORT[m - 1]} ${String(y).slice(2)}` : MONTHS_SHORT[m - 1]!,
        value: chars,
        tooltip: `${formatMonthYear(new Date(y, m - 1, 1))}: ${formatCompact(chars)} characters`,
      };
    });
  });

  /** Every key in order, zero where the server sent no row for it. */
  function fillBuckets(items: ProfileCount[] | undefined, keys: string[]) {
    const counts = new Map((items ?? []).map((i) => [i.key, i.count]));
    return keys.map((key) => ({ key, count: counts.get(key) ?? 0 }));
  }

  function countColumns(items: { key: string; count: number }[], label: (key: string) => string, noun = 'titles'): ProfileColumn[] {
    return items.map((i) => ({
      key: i.key,
      label: label(i.key),
      value: i.count,
      tooltip: `${i.count.toLocaleString('en-US')} ${i.count === 1 ? noun.replace(/s$/, '') : noun}`,
    }));
  }

  const difficultyColumns = computed(() => {
    const counts = difficultyNames.slice(0, EXPERT_BAND + 1).map(() => 0);
    for (const d of stats.value?.difficulty ?? []) counts[shelfDifficultyBand(Number(d.key))]! += d.count;
    return countColumns(
      counts.map((count, i) => ({ key: String(i), count })),
      (k) => difficultyNames[Number(k)]!
    ).map((col) => ({ ...col, colour: chartColour(Number(col.key)) }));
  });

  const coverageColumns = computed(() => {
    return countColumns(fillBuckets(stats.value?.coverageAtFinish, ['<70', '70', '75', '80', '85', '90', '95']), (k) => (k === '<70' ? '<70%' : `${k}%`));
  });

  const scoreColumns = computed(() =>
    countColumns(
      fillBuckets(
        stats.value?.scores,
        Array.from({ length: 10 }, (_, i) => String(i + 1))
      ),
      (k) => k
    )
  );

  const LENGTH_BUCKETS: { key: string; label: string; range: string }[] = [
    { key: '<50k', label: '<50k', range: 'under 50k' },
    { key: '50k', label: '50k–100k', range: '50k to 100k' },
    { key: '100k', label: '100k–250k', range: '100k to 250k' },
    { key: '250k', label: '250k–500k', range: '250k to 500k' },
    { key: '500k', label: '500k–1M', range: '500k to 1M' },
    { key: '1M', label: '>1M', range: 'over 1M' },
  ];
  const lengthColumns = computed(() =>
    countColumns(
      fillBuckets(
        stats.value?.lengths,
        LENGTH_BUCKETS.map((b) => b.key)
      ),
      (k) => LENGTH_BUCKETS.find((b) => b.key === k)!.label
    ).map((col) => ({ ...col, tooltip: `${col.tooltip}, ${LENGTH_BUCKETS.find((b) => b.key === col.key)!.range} characters` }))
  );

  const yearColumns = computed(() => {
    const years = stats.value?.releaseYears ?? [];
    if (!years.length) return [];
    const first = Number(years[0]!.key);
    const last = Number(years[years.length - 1]!.key);
    return countColumns(
      fillBuckets(
        years,
        Array.from({ length: last - first + 1 }, (_, i) => String(first + i))
      ),
      (k) => k
    );
  });

  const mediaTypeBars = computed<ProfileBar[]>(() => {
    const items = stats.value?.mediaTypes ?? [];
    const max = Math.max(1, ...items.map((i) => i.characters));
    return items.map((i) => ({
      key: i.key,
      label: getMediaTypeText(Number(i.key)),
      fraction: i.characters / max,
      value: i.count === 0 ? 'In progress' : `${i.count} ${i.count === 1 ? 'title' : 'titles'}`,
      tooltip: `About ${formatApprox(i.characters)} characters`,
    }));
  });

  const genreBars = computed<ProfileBar[]>(() => {
    const rows = genreRows((stats.value?.genres ?? []).filter((g) => g.share >= 0.01).slice(0, 10));
    const max = Math.max(0.0001, ...rows.map((r) => r.share));
    return rows.map((r) => ({ key: r.key, label: r.label, swatch: r.swatch, fraction: r.share / max, value: `${r.percent}%`, tooltip: r.tooltip }));
  });

  const selectedType = ref('global');
  const typeTabs = computed(() => [
    { label: 'All', value: 'global' },
    ...(accomplishments.value ?? [])
      .filter((a) => a.mediaType !== null)
      .map((a) => ({ label: getMediaTypeText(a.mediaType!), value: String(a.mediaType) }))
      .sort((a, b) => a.label.localeCompare(b.label)),
  ]);
  const selectedAccomplishment = computed(() =>
    (accomplishments.value ?? []).find((a) => (selectedType.value === 'global' ? a.mediaType === null : String(a.mediaType) === selectedType.value))
  );
  const accomplishmentTiles = computed(() => {
    const a = selectedAccomplishment.value;
    if (!a) return [];
    const completed = getCompletedDisplay(a.mediaType, a.completedUnitCount, a.completedDeckCount);
    const inProgress = a.unfinishedCharacterCount ?? 0;
    const tiles: { label: string; value: string; sub?: string }[] = [
      { label: completed?.label ?? 'Completed', value: (completed?.value ?? a.completedDeckCount).toLocaleString('en-US'), sub: completed?.sub },
    ];
    if (stats.value?.characters != null)
      tiles.push({
        label: 'Characters',
        value: (a.totalCharacterCount + inProgress).toLocaleString('en-US'),
        sub: inProgress > 0 ? `Including ${inProgress.toLocaleString('en-US')} in titles in progress` : undefined,
      });
    tiles.push(
      { label: 'Words', value: a.totalWordCount.toLocaleString('en-US') },
      { label: 'Unique words', value: a.uniqueWordCount.toLocaleString('en-US') },
      { label: 'Words seen once', value: a.uniqueWordUsedOnceCount.toLocaleString('en-US') },
      { label: 'Unique kanji', value: a.uniqueKanjiCount.toLocaleString('en-US') }
    );
    return tiles;
  });

  const isEmpty = computed(() => !!stats.value && stats.value.lengths.length === 0);

  /** Three across on wide screens, or two by two when scores make it four, so no chart sits alone in a row. */
  const columnChartCount = computed(() => 2 + (stats.value?.coverageAtFinish.length ? 1 : 0) + (stats.value?.scores.length ? 1 : 0));
  const columnChartGrid = computed(() => (columnChartCount.value === 3 ? 'lg:grid-cols-2 xl:grid-cols-3' : 'lg:grid-cols-2'));

  useHead(() => ({ title: `${handle.value} - Stats` }));
</script>

<template>
  <ProfileTabGate section="stats">
    <div v-if="status === 'pending' && !stats" class="flex flex-col gap-5">
      <Skeleton height="6rem" />
      <Skeleton height="18rem" />
    </div>

    <Message v-else-if="error" severity="error" :closable="false">Couldn't load these stats. Refresh the page to try again.</Message>

    <div v-else-if="stats" class="flex flex-col gap-5">
      <dl class="grid grid-cols-2 gap-4" :class="{ 'sm:grid-cols-3': tiles.length > 2 }">
        <ProfileStatTile
          v-for="tile in tiles"
          :key="tile.label"
          :label="tile.label"
          :value="tile.value"
          class="rounded-xl border border-surface-200 bg-surface-0 p-4 dark:border-surface-700 dark:bg-surface-900"
        />
      </dl>

      <ProfileGrowthSummary v-if="isOwner" />

      <Card v-if="accomplishmentTiles.length">
        <template #title>
          <h2 class="text-base font-semibold">Totals by media type</h2>
        </template>
        <template #content>
          <Tabs v-model:value="selectedType" :show-navigators="false" class="mb-4">
            <TabList class="flex-wrap">
              <Tab v-for="tab in typeTabs" :key="tab.value" :value="tab.value">{{ tab.label }}</Tab>
            </TabList>
          </Tabs>
          <dl class="grid grid-cols-2 gap-4 md:grid-cols-3">
            <div v-for="tile in accomplishmentTiles" :key="tile.label">
              <dt class="text-sm text-muted-color">{{ tile.label }}</dt>
              <dd class="text-xl font-bold tabular-nums">{{ tile.value }}</dd>
              <dd v-if="tile.sub" class="text-xs tabular-nums text-muted-color">{{ tile.sub }}</dd>
            </div>
          </dl>
        </template>
      </Card>

      <ProfileReadableCard v-if="stats.readable" :readable="stats.readable" :name="isOwner ? null : (header?.displayName ?? null)" />

      <Message v-if="isEmpty" severity="info" :closable="false">
        {{ isOwner ? 'Mark a title as Completed and your stats will start filling in.' : 'No finished titles yet.' }}
      </Message>

      <template v-else>
        <Card v-if="charsPerMonth.length">
          <template #content>
            <ProfileColumnChart
              title="Characters finished per month"
              :columns="charsPerMonth"
              show-values="fit"
              :column-width="MONTH_COLUMN_WIDTH"
              :caption="charsPerMonth.every((c) => c.value === 0) ? 'Nothing finished in the last 12 months.' : undefined"
              clip-outliers
            >
              <template #actions>
                <SelectButton
                  v-model="monthRange"
                  :options="monthRangeOptions"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  aria-label="Months shown"
                  size="small"
                />
              </template>
            </ProfileColumnChart>
          </template>
        </Card>

        <div class="grid grid-cols-1 gap-5 lg:grid-cols-2">
          <Card v-if="mediaTypeBars.length" :class="{ 'lg:col-span-2': !genreBars.length }">
            <template #content>
              <ProfileBarList title="Media types, by characters" :bars="mediaTypeBars" />
            </template>
          </Card>
          <Card v-if="genreBars.length">
            <template #content>
              <ProfileBarList title="Genres, by characters" :bars="genreBars" caption="Share of characters read in titles with this genre" />
            </template>
          </Card>
        </div>

        <div class="grid grid-cols-1 gap-5" :class="columnChartGrid">
          <Card>
            <template #content>
              <ProfileColumnChart title="Difficulty of finished titles" :columns="difficultyColumns" />
            </template>
          </Card>
          <Card v-if="stats.coverageAtFinish.length">
            <template #content>
              <ProfileColumnChart
                title="Coverage when finishing"
                :columns="coverageColumns"
                caption="Share of a title's words known on the day it was finished"
              />
            </template>
          </Card>
          <Card v-if="stats.scores.length">
            <template #content>
              <ProfileColumnChart title="Scores given" :columns="scoreColumns" />
            </template>
          </Card>
          <Card :class="{ 'lg:max-xl:col-span-2': columnChartCount === 3 }">
            <template #content>
              <ProfileColumnChart title="Length of finished titles" :columns="lengthColumns" caption="In characters" />
            </template>
          </Card>
        </div>

        <Card v-if="stats.ladder.length >= 2 || stats.undatedFinished > 0">
          <template #title>
            <h2 class="text-base font-semibold">Difficulty of finished titles over time</h2>
          </template>
          <template #subtitle>
            <span class="text-xs">
              Each dot is a title, placed on the day it was finished.{{ stats.ladder.some((p) => p.deckId != null) ? ' Select one to open it.' : '' }}
            </span>
          </template>
          <template #content>
            <ProfileLadderChart v-if="stats.ladder.length >= 2" :points="stats.ladder" />
            <p v-if="stats.undatedFinished > 0" class="mt-2 text-sm text-muted-color">
              {{ stats.undatedFinished }} finished {{ stats.undatedFinished === 1 ? 'title has' : 'titles have' }} no finish date, so
              {{ stats.undatedFinished === 1 ? "it isn't" : "they aren't" }} shown.
              <NuxtLink v-if="isOwner" to="/settings/media-list" class="!text-primary-600 dark:!text-primary-400">Import dates from AniList or VNDB</NuxtLink>
            </p>
          </template>
        </Card>

        <Card v-if="yearColumns.length">
          <template #content>
            <ProfileColumnChart title="Release year of finished titles" :columns="yearColumns" :visible-columns="15" />
          </template>
        </Card>
      </template>
    </div>
  </ProfileTabGate>
</template>
