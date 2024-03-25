﻿// Copyright (c) 2011-2023 Roland Pheasant. All rights reserved.
// Roland Pheasant licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Reactive.Linq;
using DynamicData.Binding;

namespace DynamicData.Cache.Internal;

internal sealed class SortAndVirtualize<TObject, TKey>(IObservable<IChangeSet<TObject, TKey>> source,
    IComparer<TObject> comparer,
    IObservable<IVirtualRequest> virtualRequests,
    SortAndVirtualizeOptions options)
    where TObject : notnull
    where TKey : notnull
{
    private readonly IObservable<IChangeSet<TObject, TKey>> _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly IObservable<IVirtualRequest> _virtualRequests = virtualRequests ?? throw new ArgumentNullException(nameof(virtualRequests));

    public IObservable<IVirtualChangeSet<TObject, TKey>> Run() =>
        Observable.Create<IVirtualChangeSet<TObject, TKey>>(
            observer =>
            {
                var locker = new object();

                IVirtualRequest virtualParams = new VirtualRequest();

                var sortedList = new List<KeyValuePair<TKey, TObject>>(options.InitialCapacity);
                var virtualItems = new List<KeyValuePair<TKey, TObject>>(50);
                var keyValueComparer = new KeyValueComparer<TObject, TKey>(comparer);

                var sortOptions = new SortAndBindOptions
                {
                    UseBinarySearch = options.UseBinarySearch,
                    ResetThreshold = options.ResetThreshold
                };

                var applicator = new SortedKeyValueApplicator<TObject, TKey>(sortedList, keyValueComparer, sortOptions);

                var paramsChanged = _virtualRequests.Synchronize(locker)
                    .DistinctUntilChanged()
                    // exclude dodgy params
                    .Where(parameters => parameters is { StartIndex: > 0, Size: > 0 })
                    .Select(request =>
                    {
                        virtualParams = request;

                        // re-apply virtual changes
                        return ApplyVirtualChanges();
                    });

                var dataChange = _source.Synchronize(locker)
                    .Select(changes =>
                    {
                        // apply changes to the sorted list
                        applicator.ProcessChanges(changes);

                        // re-apply virtual changes
                        return ApplyVirtualChanges(changes);
                    });

                return paramsChanged.Merge(dataChange).SubscribeSafe(observer);

                VirtualChangeSet<TObject, TKey> ApplyVirtualChanges(IChangeSet<TObject, TKey>? changeSet = null)
                {
                    // re-calculate virtual changes
                    var currentVirtualItems = new List<KeyValuePair<TKey, TObject>>(virtualParams.Size);
                    currentVirtualItems.AddRange(sortedList.Skip(virtualParams.StartIndex).Take(virtualParams.Size));

                    // calculate notifications
                    var notifications = CalculateVirtualChanges(currentVirtualItems, virtualItems, changeSet);

                    // set current result
                    virtualItems = currentVirtualItems;

                    // adapt old defaults with current
                    var optimisations = options.UseBinarySearch
                        ? SortOptimisations.ComparesImmutableValuesOnly
                        : SortOptimisations.None;

                    var readonlyItems = new KeyValueCollection<TObject, TKey>(virtualItems, keyValueComparer, SortReason.DataChanged, optimisations);
                    var response = new VirtualResponse(virtualParams.Size, virtualParams.StartIndex, sortedList.Count);
                    return new VirtualChangeSet<TObject, TKey>(notifications, readonlyItems, response);
                }
            });

    public static ChangeSet<TObject, TKey> CalculateVirtualChanges(List<KeyValuePair<TKey, TObject>> currentItems, List<KeyValuePair<TKey, TObject>> previousItems, IChangeSet<TObject, TKey>? changes = null)
    {
        var keyComparer = new KeyComparer<TObject, TKey>();
        var result = new ChangeSet<TObject, TKey>(currentItems.Count * 2);

        var removes = previousItems.Except(currentItems, keyComparer).Select(kvp => new Change<TObject, TKey>(ChangeReason.Remove, kvp.Key, kvp.Value));
        var adds = currentItems.Except(previousItems, keyComparer).Select(kvp => new Change<TObject, TKey>(ChangeReason.Remove, kvp.Key, kvp.Value));

        result.AddRange(removes);
        result.AddRange(adds);

        if (changes is null) return result;

        var inBothKeys = new HashSet<TKey>(previousItems.Intersect(currentItems, keyComparer).Select(x => x.Key));

        foreach (var change in changes)
        {
            if (!inBothKeys.Contains(change.Key)) continue;

            if (change.Reason is ChangeReason.Update or ChangeReason.Refresh)
            {
                result.Add(change);
            }
        }

        return result;
    }
}
