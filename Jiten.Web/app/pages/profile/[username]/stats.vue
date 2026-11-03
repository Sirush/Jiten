<script setup lang="ts">
  import {
    MediaType,
    type ProfileBar,
    type ProfileBucket,
    type ProfileChartMeasure,
    type ProfileColumn,
    type ProfileStats,
    type UserAccomplishment,
  } from '~/types';
  import { getCompletedDisplay, getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { difficultyNames, EXPERT_BAND, shelfDifficultyBand } from '~/utils/difficultyColours';
  import { formatApprox, formatCompact } from '~/utils/siteStats';
  import { formatMonthYear, MONTHS_SHORT } from '~/utils/dateFormat';
  import { genreRows } from '~/utils/profileGenres';
  import { useJitenStore } from '~/stores/jitenStore';

  const { handle, header, isOwner, viewAsQuery, apiPath } = useProfileContext();
  const { chartColour } = useDifficultyColours();
  const jitenStore = useJitenStore();
  const route = useRoute();
  const router = useRouter();

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

  function parseTypeQuery(value: unknown): MediaType | null {
    const parsed = Number(Array.isArray(value) ? value[0] : value);
    const known = Object.values(MediaType).filter((v) => typeof v === 'number') as MediaType[];
    return known.includes(parsed as MediaType) ? (parsed as MediaType) : null;
  }

  const mediaType = ref<MediaType | null>(parseTypeQuery(route.query.type));
  watch(mediaType, (value) => router.replace({ query: { ...route.query, type: value ?? undefined } }));

  /** Types with a visible finished title or with reading totals, so every type the totals card knows stays reachable. */
  const mediaTypeOptions = computed(() => {
    const finished = new Map<MediaType, number>();
    for (const row of stats.value?.lengths ?? []) finished.set(row.mediaType, (finished.get(row.mediaType) ?? 0) + row.count);
    const types = new Set<MediaType>([...finished.keys(), ...(accomplishments.value ?? []).flatMap((a) => (a.mediaType === null ? [] : [a.mediaType]))]);
    const total = [...finished.values()].reduce((sum, n) => sum + n, 0);
    return [
      { label: 'All types', value: null as MediaType | null, count: total },
      ...[...types]
        .map((t) => ({ label: getMediaTypeText(t), value: t as MediaType | null, count: finished.get(t) ?? 0 }))
        .sort((a, b) => a.label.localeCompare(b.label)),
    ];
  });

  const ofType = <T extends { mediaType: MediaType }>(rows: T[] | undefined | null) =>
    (rows ?? []).filter((r) => mediaType.value === null || r.mediaType === mediaType.value);

  /** The chosen measure of one chart, kept in a cookie so the server render already draws it. */
  function measureModel(chart: string, fallback: ProfileChartMeasure = 'titles') {
    return computed<ProfileChartMeasure>({
      get: () => jitenStore.profileChartMeasures[chart] ?? fallback,
      set: (value) => (jitenStore.profileChartMeasures = { ...jitenStore.profileChartMeasures, [chart]: value }),
    });
  }
  const measureOptions: { label: string; value: ProfileChartMeasure }[] = [
    { label: 'Titles', value: 'titles' },
    { label: 'Characters', value: 'characters' },
  ];
  const monthMeasure = measureModel('months', 'characters');
  const difficultyMeasure = measureModel('difficulty');
  const coverageMeasure = measureModel('coverage');
  const lengthMeasure = measureModel('length');
  const yearMeasure = measureModel('releaseYear');

  const groupingOptions: { label: string; value: 'month' | 'year' }[] = [
    { label: 'Month', value: 'month' },
    { label: 'Year', value: 'year' },
  ];

  const monthRange = ref<'year' | 'all'>('year');
  const monthRangeOptions = [
    { label: '12 months', value: 'year' },
    { label: 'All time', value: 'all' },
  ];
  /** Width that fits a label such as "Jan 26" and a value such as "99.4K" on every column, so none are thinned out. */
  const MONTH_COLUMN_WIDTH = 40;

  const titlesText = (count: number) => `${count.toLocaleString('en-US')} ${count === 1 ? 'title' : 'titles'}`;

  const profileMonthGroupingIsMonth = computed(() => jitenStore.profileMonthGrouping !== 'year');

  const periodColumns = computed<ProfileColumn[]>(() => {
    const rows = ofType(stats.value?.charactersByMonth);
    if (!rows.length) return [];
    const byYear = !profileMonthGroupingIsMonth.value;
    const totals = new Map<string, { characters: number; titles: number }>();
    for (const row of rows) {
      const key = byYear ? row.month.slice(0, 4) : row.month;
      const total = totals.get(key) ?? { characters: 0, titles: 0 };
      total.characters += row.characters;
      total.titles += row.titles;
      totals.set(key, total);
    }
    const column = (key: string, label: string, name: string): ProfileColumn => {
      const total = totals.get(key) ?? { characters: 0, titles: 0 };
      return monthMeasure.value === 'titles'
        ? { key, label, value: total.titles, tooltip: `${name}: ${titlesText(total.titles)}` }
        : { key, label, value: total.characters, tooltip: `${name}: ${formatCompact(total.characters)} characters` };
    };

    const now = new Date();
    if (byYear) {
      const first = Number(rows[0]!.month.slice(0, 4));
      return Array.from({ length: now.getFullYear() - first + 1 }, (_, i) => String(first + i)).map((y) => column(y, y, y));
    }

    const [firstYear, firstMonth] =
      monthRange.value === 'all'
        ? (rows[0]!.month.split('-').map(Number) as [number, number])
        : [now.getMonth() >= 11 ? now.getFullYear() : now.getFullYear() - 1, ((now.getMonth() + 1) % 12) + 1];
    const total = (now.getFullYear() - firstYear) * 12 + now.getMonth() + 1 - firstMonth + 1;
    return Array.from({ length: total }, (_, i) => {
      const y = firstYear + Math.floor((firstMonth - 1 + i) / 12);
      const m = ((firstMonth - 1 + i) % 12) + 1;
      return column(
        `${y}-${String(m).padStart(2, '0')}`,
        m === 1 || i === 0 ? `${MONTHS_SHORT[m - 1]} ${String(y).slice(2)}` : MONTHS_SHORT[m - 1]!,
        formatMonthYear(new Date(y, m - 1, 1))
      );
    });
  });
  const periodTitle = computed(
    () => `${monthMeasure.value === 'titles' ? 'Titles' : 'Characters'} finished per ${jitenStore.profileMonthGrouping === 'year' ? 'year' : 'month'}`
  );

  /** Every key in order, zero where no row of the selected media type falls in it. */
  function bucketColumns(
    rows: ProfileBucket[] | undefined,
    keys: string[],
    options: { label: (key: string) => string; measure: ProfileChartMeasure; keyOf?: (row: ProfileBucket) => string; detail?: (key: string) => string }
  ): ProfileColumn[] {
    const totals = new Map<string, { count: number; characters: number }>();
    for (const row of ofType(rows)) {
      const key = options.keyOf?.(row) ?? row.key;
      const total = totals.get(key) ?? { count: 0, characters: 0 };
      total.count += row.count;
      total.characters += row.characters;
      totals.set(key, total);
    }
    return keys.map((key) => {
      const { count, characters } = totals.get(key) ?? { count: 0, characters: 0 };
      const main = options.measure === 'characters' ? `About ${formatApprox(characters)} characters in ${titlesText(count)}` : titlesText(count);
      const detail = options.detail?.(key);
      return {
        key,
        label: options.label(key),
        value: options.measure === 'characters' ? characters : count,
        tooltip: detail ? `${main}, ${detail}` : main,
      };
    });
  }

  const charactersTitle = (measure: ProfileChartMeasure, titles: string, characters: string) => (measure === 'characters' ? characters : titles);

  const difficultyColumns = computed(() =>
    bucketColumns(
      stats.value?.difficulty,
      difficultyNames.slice(0, EXPERT_BAND + 1).map((_, i) => String(i)),
      { label: (k) => difficultyNames[Number(k)]!, measure: difficultyMeasure.value, keyOf: (row) => String(shelfDifficultyBand(Number(row.key))) }
    ).map((col) => ({ ...col, colour: chartColour(Number(col.key)) }))
  );

  const coverageColumns = computed(() =>
    bucketColumns(stats.value?.coverageAtFinish, ['<70', '70', '75', '80', '85', '90', '95'], {
      label: (k) => (k === '<70' ? '<70%' : `${k}%`),
      measure: coverageMeasure.value,
    })
  );

  const scoreColumns = computed(() =>
    bucketColumns(
      stats.value?.scores,
      Array.from({ length: 10 }, (_, i) => String(i + 1)),
      { label: (k) => k, measure: 'titles' }
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
    bucketColumns(
      stats.value?.lengths,
      LENGTH_BUCKETS.map((b) => b.key),
      {
        label: (k) => LENGTH_BUCKETS.find((b) => b.key === k)!.label,
        measure: lengthMeasure.value,
        detail: (k) => `${LENGTH_BUCKETS.find((b) => b.key === k)!.range} characters each`,
      }
    )
  );

  const yearColumns = computed(() => {
    const years = ofType(stats.value?.releaseYears).map((r) => Number(r.key));
    if (!years.length) return [];
    const first = Math.min(...years);
    const last = Math.max(...years);
    return bucketColumns(
      stats.value?.releaseYears,
      Array.from({ length: last - first + 1 }, (_, i) => String(first + i)),
      { label: (k) => k, measure: yearMeasure.value }
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
  const showMediaTypeBars = computed(() => mediaType.value === null && mediaTypeBars.value.length > 0);

  const genreBars = computed<ProfileBar[]>(() => {
    const shares =
      mediaType.value === null ? stats.value?.genres : stats.value?.genresByMediaType?.find((t) => t.mediaType === mediaType.value)?.genres;
    const rows = genreRows((shares ?? []).filter((g) => g.share >= 0.01).slice(0, 10));
    const max = Math.max(0.0001, ...rows.map((r) => r.share));
    return rows.map((r) => ({ key: r.key, label: r.label, swatch: r.swatch, fraction: r.share / max, value: `${r.percent}%`, tooltip: r.tooltip }));
  });

  const selectedAccomplishment = computed(() => (accomplishments.value ?? []).find((a) => a.mediaType === mediaType.value));
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
  const typeHasFinished = computed(() => ofType(stats.value?.lengths).length > 0);
  const hasCoverage = computed(() => ofType(stats.value?.coverageAtFinish).length > 0);
  const hasScores = computed(() => ofType(stats.value?.scores).length > 0);

  /** Three across on wide screens, or two by two when scores make it four, so no chart sits alone in a row. */
  const columnChartCount = computed(() => 2 + (hasCoverage.value ? 1 : 0) + (hasScores.value ? 1 : 0));
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

      <ProfileReadableCard v-if="stats.readable" :readable="stats.readable" :name="isOwner ? null : (header?.displayName ?? null)" />

      <div v-if="mediaTypeOptions.length > 2" class="flex justify-end">
        <Select
          v-model="mediaType"
          :options="mediaTypeOptions"
          option-label="label"
          option-value="value"
          placeholder="All types"
          aria-label="Filter stats by media type"
          size="small"
          scroll-height="50vh"
          class="w-full sm:w-56"
        >
          <template #option="{ option }">
            <div class="flex w-full items-center justify-between gap-3" :class="{ 'text-muted-color': option.count === 0 }">
              <span>{{ option.label }}</span>
              <span class="text-xs tabular-nums text-muted-color">{{ option.count }}</span>
            </div>
          </template>
        </Select>
      </div>

      <Card v-if="accomplishmentTiles.length">
        <template #title>
          <h2 class="text-base font-semibold">{{ mediaType === null ? 'Totals' : `${getMediaTypeText(mediaType)} totals` }}</h2>
        </template>
        <template #content>
          <dl class="grid grid-cols-2 gap-4 md:grid-cols-3">
            <div v-for="tile in accomplishmentTiles" :key="tile.label">
              <dt class="text-sm text-muted-color">{{ tile.label }}</dt>
              <dd class="text-xl font-bold tabular-nums">{{ tile.value }}</dd>
              <dd v-if="tile.sub" class="text-xs tabular-nums text-muted-color">{{ tile.sub }}</dd>
            </div>
          </dl>
        </template>
      </Card>

      <Message v-if="isEmpty" severity="info" :closable="false">
        {{ isOwner ? 'Mark a title as Completed and your stats will start filling in.' : 'No finished titles yet.' }}
      </Message>

      <template v-else>
        <Card v-if="periodColumns.length">
          <template #content>
            <ProfileColumnChart
              :title="periodTitle"
              :columns="periodColumns"
              show-values="fit"
              :column-width="profileMonthGroupingIsMonth ? MONTH_COLUMN_WIDTH : undefined"
              :caption="
                profileMonthGroupingIsMonth && monthRange === 'year' && periodColumns.every((c) => c.value === 0)
                  ? 'Nothing finished in the last 12 months.'
                  : undefined
              "
              clip-outliers
            >
              <template #actions>
                <div class="flex items-center gap-1">
                  <SelectButton
                    v-if="profileMonthGroupingIsMonth"
                    v-model="monthRange"
                    :options="monthRangeOptions"
                    option-label="label"
                    option-value="value"
                    :allow-empty="false"
                    aria-label="Months shown"
                    size="small"
                  />
                  <ProfileChartSettings>
                    <ProfileChartChoice v-model="monthMeasure" label="Count" :options="measureOptions" />
                    <ProfileChartChoice v-model="jitenStore.profileMonthGrouping" label="Group by" :options="groupingOptions" />
                  </ProfileChartSettings>
                </div>
              </template>
            </ProfileColumnChart>
          </template>
        </Card>

        <div v-if="showMediaTypeBars || genreBars.length" class="grid grid-cols-1 gap-5 lg:grid-cols-2">
          <Card v-if="showMediaTypeBars" :class="{ 'lg:col-span-2': !genreBars.length }">
            <template #content>
              <ProfileBarList title="Media types, by characters" :bars="mediaTypeBars" />
            </template>
          </Card>
          <Card v-if="genreBars.length" :class="{ 'lg:col-span-2': !showMediaTypeBars }">
            <template #content>
              <ProfileBarList title="Genres, by characters" :bars="genreBars" caption="Share of characters read in titles with this genre" />
            </template>
          </Card>
        </div>

        <Message v-if="!typeHasFinished" severity="info" :closable="false">
          Nothing of this type finished yet.
        </Message>

        <template v-else>
          <div class="grid grid-cols-1 gap-5" :class="columnChartGrid">
            <Card>
              <template #content>
                <ProfileColumnChart
                  :title="charactersTitle(difficultyMeasure, 'Difficulty of finished titles', 'Characters read by difficulty')"
                  :columns="difficultyColumns"
                >
                  <template #actions>
                    <ProfileChartSettings>
                      <ProfileChartChoice v-model="difficultyMeasure" label="Count" :options="measureOptions" />
                    </ProfileChartSettings>
                  </template>
                </ProfileColumnChart>
              </template>
            </Card>
            <Card v-if="hasCoverage">
              <template #content>
                <ProfileColumnChart
                  :title="charactersTitle(coverageMeasure, 'Coverage when finishing', 'Characters read by coverage when finishing')"
                  :columns="coverageColumns"
                  caption="Share of a title's words known on the day it was finished"
                >
                  <template #actions>
                    <ProfileChartSettings>
                      <ProfileChartChoice v-model="coverageMeasure" label="Count" :options="measureOptions" />
                    </ProfileChartSettings>
                  </template>
                </ProfileColumnChart>
              </template>
            </Card>
            <Card v-if="hasScores">
              <template #content>
                <ProfileColumnChart title="Scores given" :columns="scoreColumns" />
              </template>
            </Card>
            <Card :class="{ 'lg:max-xl:col-span-2': columnChartCount === 3 }">
              <template #content>
                <ProfileColumnChart
                  :title="charactersTitle(lengthMeasure, 'Length of finished titles', 'Characters read by title length')"
                  :columns="lengthColumns"
                  caption="Title length, in characters"
                >
                  <template #actions>
                    <ProfileChartSettings>
                      <ProfileChartChoice v-model="lengthMeasure" label="Count" :options="measureOptions" />
                    </ProfileChartSettings>
                  </template>
                </ProfileColumnChart>
              </template>
            </Card>
          </div>

          <ProfileLadderCard :media-type="mediaType" />

          <Card v-if="yearColumns.length">
            <template #content>
              <ProfileColumnChart
                :title="charactersTitle(yearMeasure, 'Release year of finished titles', 'Characters read by release year')"
                :columns="yearColumns"
                :visible-columns="15"
              >
                <template #actions>
                  <ProfileChartSettings>
                    <ProfileChartChoice v-model="yearMeasure" label="Count" :options="measureOptions" />
                  </ProfileChartSettings>
                </template>
              </ProfileColumnChart>
            </template>
          </Card>
        </template>
      </template>
    </div>
  </ProfileTabGate>
</template>
