using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class ScanIntroRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public ScanIntroRouteHandler(
            Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems)
        {
            _expandToTargetItems = expandToTargetItems;
        }

        public MediaInfoMenuResponse Handle(ScanIntroRequest request)
        {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0)
            {
                response.Message = "no items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu ScanIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids).OfType<Episode>().ToList();
            response.Total = targetItems.Count;

            if (targetItems.Count == 0)
            {
                response.Message = "no supported items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu ScanIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            if (Plugin.IntroScanService == null)
            {
                response.Message = "intro scan service unavailable";
                response.Failed = targetItems.Count;
                Plugin.Instance.Logger.Error("ShortcutMenu ScanIntro failed: IntroScanService is null.");
                return response;
            }

            foreach (var episode in targetItems)
            {
                response.Processed++;
                try
                {
                    if (Plugin.IntroScanService.QueueEpisodeScan(episode, "ShortcutMenu"))
                    {
                        response.Succeeded++;
                        Plugin.Instance.Logger.Info($"ShortcutMenu 扫描片头已入队: {episode.Path ?? episode.Name}");
                    }
                    else
                    {
                        response.Skipped++;
                    }
                }
                catch (Exception ex)
                {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单扫描片头失败: {episode.Path ?? episode.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = response.Succeeded > 0 ? "scan intro queued" : "no episodes queued";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu ScanIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }
    }
}
