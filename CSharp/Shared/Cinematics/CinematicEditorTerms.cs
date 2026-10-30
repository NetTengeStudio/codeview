namespace ScpItdMod.Cinematics {
    public enum ЗапускСцены : byte { НачалоРаунда = CinematicSceneTrigger.RoundStart, ПоСигналу = CinematicSceneTrigger.Signal, Вручную = CinematicSceneTrigger.Manual }
    public enum ПереходКадра : byte {
        ПоВремени = CinematicShotAdvance.AfterTime, ВертолётГотов = CinematicShotAdvance.VehicleReady, МаршрутПройден = CinematicShotAdvance.RouteDone,
        Посадка = CinematicShotAdvance.Landed, ТочкаПройдена = CinematicShotAdvance.Waypoint, ЭкипажЗакончил = CinematicShotAdvance.ActorsDone, Сигнал = CinematicShotAdvance.Signal, УдалениеЗавершено = CinematicShotAdvance.Despawned, КонтактШасси = CinematicShotAdvance.Contact
    }
    public enum ВетвьКадра : byte {
        Нет = CinematicShotBranch.None, ОпасныйПолёт = CinematicShotBranch.VehicleUnsafe, ТочкаПройдена = CinematicShotBranch.Waypoint,
        Посадка = CinematicShotBranch.Landed, МаршрутПройден = CinematicShotBranch.RouteDone, Сигнал = CinematicShotBranch.Signal
    }
    public enum ВидКамеры : byte { Неподвижная = CinematicCameraKind.Fixed, Проезд = CinematicCameraKind.Dolly, Следящая = CinematicCameraKind.Follow }
    public enum ВидТочки : byte { Пролёт = CinematicWaypointKind.FlyThrough, Зависание = CinematicWaypointKind.Hover, Посадка = CinematicWaypointKind.Land }
    public enum КурсВертолёта : byte { Авто = CinematicHeadingKind.Auto, Влево = CinematicHeadingKind.FaceLeft, Вправо = CinematicHeadingKind.FaceRight }
    public enum ДействиеЭкипажа : byte {
        Высадка = CinematicActorKind.Disembark, ИдтиК = CinematicActorKind.MoveTo, Повернуться = CinematicActorKind.Face, Поза = CinematicActorKind.Pose, Посадка = CinematicActorKind.Board
    }
    public enum КтоИсполняет : byte { ВсеПассажиры = CinematicActorGroup.AllPassengers, Место = CinematicActorGroup.Seat }
    public enum ПозаЭкипажа : byte { Стоя = CinematicPoseKind.Stand, Присед = CinematicPoseKind.Crouch, Прицел = CinematicPoseKind.Aim }
    public enum ВидЭффекта : byte {
        Звук = CinematicEffectKind.Sound, Частицы = CinematicEffectKind.Particles, ПотокРотора = CinematicEffectKind.RotorWash, Свет = CinematicEffectKind.Light,
        Сигнал = CinematicEffectKind.Signal, ЗапускДвигателей = CinematicEffectKind.EngineOn, ОстановДвигателей = CinematicEffectKind.EngineOff,
        Рамка = CinematicEffectKind.Letterbox, Затемнение = CinematicEffectKind.Fade, Титр = CinematicEffectKind.Title, Удаление = CinematicEffectKind.Despawn
    }
}
