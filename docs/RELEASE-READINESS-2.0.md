# Boostix 2.0 — выход из BETA

Этот список отделяет то, что можно доказать автоматически, от внешних условий.
До выполнения всех блокеров 2.0 публикуется только как `BETA`/prerelease.

## Автоматизируемые блокеры

- [ ] Exact Game Target: ни один чужой foreground‑процесс не получает приоритет.
- [ ] Исходный приоритет и временный план питания восстановлены максимум за 2 с
  после потери фокуса, выхода, сбоя и закрытия Boostix.
- [ ] Session Guard игнорирует одиночный spike, имеет hysteresis/cooldown и
  ограниченный ring buffer.
- [ ] Нет standby purge, target working-set trim, High/Realtime, affinity,
  BCDEdit и отключения Defender/UAC/Update/pagefile.
- [ ] Фоновый анализ read‑only; стандартное закрытие graceful‑only.
- [ ] Proof Mode отклоняет mismatch/малую выборку и не обещает FPS.
- [ ] Correlation вылета не выдаёт событие другого PID/процесса за причину.
- [ ] Installer/update проходит check → download → verify → install → health
  check → done и сохраняет рабочую старую версию при любом отказе.
- [ ] CodeQL, все regression‑тесты и lifecycle E2E зелёные на Windows 2022/2025.
- [ ] 100 start/stop циклов и 8‑часовой soak укладываются в CPU/RAM/handle/log
  бюджеты, зафиксированные в архитектуре.
- [ ] Keyboard‑only, Narrator, High Contrast и 100/125/150/175/200% DPI прошли
  acceptance без обрезки и fractional bitmap scaling.

## Внешние блокеры stable

- [ ] Доверенный Authenticode code-signing сертификат на **Silas Suspect**.
- [ ] Подпись каждого EXE тем же сертификатом с RFC 3161 timestamp; Windows
  показывает ожидаемого издателя, WDAC/SmartScreen не требует обхода защиты.
- [ ] Защита `main`: required CI/CodeQL, запрет force-push, review CODEOWNERS.
- [ ] Подписанный tag и защищённое release environment; build once, sign once,
  публикуются те же проверенные байты.
- [ ] Независимое бинарное зеркало, а не только GitHub raw/releases.
- [ ] Подтверждены Windows 10 22H2 и Windows 11 23H2/24H2 на реальных VM/ПК,
  UAC под другой учётной записью, proxy TLS inspection, EDR/AV lock, WDAC,
  нехватка диска и обрыв обновления 0/50/99%.
- [ ] Явно принято решение по x64/ARM64; неподдерживаемая архитектура получает
  понятный отказ до изменений.
- [ ] Не менее 20 физических ПК, 500 часов, 100 полных сессий, crash-free и
  install/update success не ниже принятых SLO.

## Канал обновления 2.x

Следующая схема манифеста должна включать `keyId`, монотонный `sequence`,
`publishedAt`, `expiresAt`, `channel`, `architecture` и минимальную допустимую
версию клиента. Клиент хранит последний принятый sequence и отклоняет replay,
просроченный manifest и неизвестный keyId. Ротация ключа оформляется отдельным
подписанным переходом; старый опубликованный asset/tag никогда не заменяется.
