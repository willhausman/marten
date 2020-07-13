using System;
using System.Collections.Generic;
using System.Linq;
using Baseline;
using Marten.Events;
using Marten.Patching;
using Marten.Schema;
using Marten.Services;
using Marten.Util;

namespace Marten.V4Internals
{
    public class UnitOfWork : IUnitOfWork, IChangeSet
    {
        private readonly List<IStorageOperation> _operations = new List<IStorageOperation>();

        public void Add(IStorageOperation operation)
        {
            _operations.Add(operation);
        }

        public IReadOnlyList<IStorageOperation> AllOperations => _operations;

        public void Sort(StoreOptions options)
        {
            if (shouldSort(options, out var comparer))
            {
                _operations.Sort(comparer);
            }
        }

        private bool shouldSort(StoreOptions options, out IComparer<IStorageOperation> comparer)
        {
            comparer = null;
            if (_operations.Count <= 1)
                return false;

            if (_operations.Select(x => x.DocumentType).Distinct().Count() == 1)
                return false;

            var types = _operations
                .Select(x => x.DocumentType)
                .Distinct()
                .TopologicalSort(type => GetTypeDependencies(options, type)).ToArray();

            if (_operations.OfType<IDeletion>().Any())
            {
                comparer = new StorageOperationWithDeletionsComparer(types);
            }
            else
            {
                comparer = new StorageOperationByTypeComparer(types);
            }

            return true;
        }

        private IEnumerable<Type> GetTypeDependencies(StoreOptions options, Type type)
        {
            var mapping = options.Storage.FindMapping(type);
            var documentMapping = mapping as DocumentMapping ?? (mapping as SubClassMapping)?.Parent;
            if (documentMapping == null)
                return Enumerable.Empty<Type>();

            return documentMapping.ForeignKeys.Where(x => x.ReferenceDocumentType != type && x.ReferenceDocumentType != null)
                .SelectMany(keyDefinition =>
                {
                    var results = new List<Type>();
                    var referenceMappingType =
                        options.Storage.FindMapping(keyDefinition.ReferenceDocumentType) as DocumentMapping;
                    // If the reference type has sub-classes, also need to insert/update them first too
                    if (referenceMappingType != null && referenceMappingType.SubClasses.Any())
                    {
                        results.AddRange(referenceMappingType.SubClasses.Select(s => s.DocumentType));
                    }
                    results.Add(keyDefinition.ReferenceDocumentType);
                    return results;
                });
        }




        IEnumerable<IDeletion> IUnitOfWork.Deletions()
        {
            return _operations.OfType<IDeletion>();
        }

        IEnumerable<IDeletion> IUnitOfWork.DeletionsFor<T>()
        {
            return _operations.OfType<IDeletion>().Where(x => x.DocumentType.CanBeCastTo<T>());
        }

        IEnumerable<IDeletion> IUnitOfWork.DeletionsFor(Type documentType)
        {
            return _operations.OfType<IDeletion>().Where(x => x.DocumentType.CanBeCastTo(documentType));
        }

        IEnumerable<object> IUnitOfWork.Updates()
        {
            return _operations
                .OfType<IDocumentStorageOperation>()
                .Where(x => x.Role() == StorageRole.Update || x.Role() == StorageRole.Upsert)
                .Select(x => x.Document);
        }

        IEnumerable<object> IUnitOfWork.Inserts()
        {
            return _operations
                .OfType<IDocumentStorageOperation>()
                .Where(x => x.Role() == StorageRole.Insert)
                .Select(x => x.Document);
        }

        IEnumerable<T> IUnitOfWork.UpdatesFor<T>()
        {
            return _operations
                .OfType<IDocumentStorageOperation>()
                .Where(x => x.Role() == StorageRole.Update || x.Role() == StorageRole.Upsert)
                .Select(x => x.Document)
                .OfType<T>();

        }

        IEnumerable<T> IUnitOfWork.InsertsFor<T>()
        {
            return _operations
                .OfType<IDocumentStorageOperation>()
                .Where(x => x.Role() == StorageRole.Insert)
                .Select(x => x.Document)
                .OfType<T>();
        }

        IEnumerable<T> IUnitOfWork.AllChangedFor<T>()
        {
            return _operations
                .OfType<IDocumentStorageOperation>()
                .Select(x => x.Document)
                .OfType<T>();
        }

        IEnumerable<EventStream> IUnitOfWork.Streams()
        {
            return _operations.OfType<AppendEventsOperation>().Select(x => x.Stream);
        }

        IEnumerable<PatchOperation> IUnitOfWork.Patches()
        {
            return _operations.OfType<PatchOperation>();
        }

        IEnumerable<IStorageOperation> IUnitOfWork.Operations()
        {
            return _operations;
        }

        IEnumerable<IStorageOperation> IUnitOfWork.OperationsFor<T>()
        {
            return _operations.Where(x => x.DocumentType.CanBeCastTo<T>());
        }

        IEnumerable<IStorageOperation> IUnitOfWork.OperationsFor(Type documentType)
        {
            return _operations.Where(x => x.DocumentType.CanBeCastTo(documentType));
        }

        IEnumerable<object> IChangeSet.Updated => _operations.OfType<IDocumentStorageOperation>().Where(x => x.Role() == StorageRole.Update || x.Role() == StorageRole.Upsert).Select(x => x.Document);

        IEnumerable<object> IChangeSet.Inserted => _operations.OfType<IDocumentStorageOperation>().Where(x => x.Role() == StorageRole.Insert).Select(x => x.Document);

        IEnumerable<IDeletion> IChangeSet.Deleted => _operations.OfType<IDeletion>();

        IEnumerable<IEvent> IChangeSet.GetEvents()
        {
            return _operations.OfType<AppendEventsOperation>().SelectMany(x => x.Stream.Events);
        }

        IEnumerable<PatchOperation> IChangeSet.Patches => _operations.OfType<PatchOperation>();

        IEnumerable<EventStream> IChangeSet.GetStreams()
        {
            return _operations.OfType<AppendEventsOperation>().Select(x => x.Stream);
        }

        private IEnumerable<IStorageOperation> operationsFor(Type documentType)
        {
            return _operations.Where(x => x.DocumentType == documentType);
        }

        public void Eject<T>(T document)
        {
            var operations = operationsFor(typeof(T));
            var matching = operations.OfType<IDocumentStorageOperation>().Where(x => object.ReferenceEquals(document, x.Document)).ToArray();

            foreach (var operation in matching)
            {
                _operations.Remove(operation);
            }
        }

        private class StorageOperationWithDeletionsComparer: IComparer<IStorageOperation>
        {
            private readonly Type[] _topologicallyOrderedTypes;

            public StorageOperationWithDeletionsComparer(Type[] topologicallyOrderedTypes)
            {
                _topologicallyOrderedTypes = topologicallyOrderedTypes;
            }

            public int Compare(IStorageOperation x, IStorageOperation y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x?.DocumentType == null || y?.DocumentType == null)
                {
                    return 0;
                }

                // Maintain order if same document type and same operation
                if (x.DocumentType == y.DocumentType && x.GetType() == y.GetType())
                {
                    return 0;
                }

                var xIndex = FindIndex(x);
                var yIndex = FindIndex(y);

                var xIsDelete = x is IDeletion;
                var yIsDelete = y is IDeletion;

                if (xIsDelete != yIsDelete)
                {
                    // Arbitrary order if one is a delete but the other is not, because this will force the sorting
                    // to try and compare these documents against others and fall in to the below checks.
                    return -1;
                }

                if (xIsDelete)
                {
                    // Both are deletes, so we need reverse topological order to inserts, updates and upserts
                    return yIndex.CompareTo(xIndex);
                }

                // Both are inserts, updates or upserts so topological
                return xIndex.CompareTo(yIndex);
            }

            private int FindIndex(IStorageOperation x)
            {
                // Will loop through up the inheritance chain until reaches the end or the index is found, used
                // to handle inheritance as topologically sorted array may not have the subclasses listed
                var documentType = x.DocumentType;
                var index = 0;

                do
                {
                    index = _topologicallyOrderedTypes.IndexOf(documentType);
                    documentType = documentType.BaseType;
                } while (index == -1 && documentType != null);

                return index;
            }
        }

        private class StorageOperationByTypeComparer: IComparer<IStorageOperation>
        {
            private readonly Type[] _topologicallyOrderedTypes;

            public StorageOperationByTypeComparer(Type[] topologicallyOrderedTypes)
            {
                _topologicallyOrderedTypes = topologicallyOrderedTypes;
            }

            public int Compare(IStorageOperation x, IStorageOperation y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x?.DocumentType == null || y?.DocumentType == null)
                {
                    return 0;
                }

                if (x.DocumentType == y.DocumentType)
                {
                    return 0;
                }

                var xIndex = FindIndex(x);
                var yIndex = FindIndex(y);

                return xIndex.CompareTo(yIndex);
            }

            private int FindIndex(IStorageOperation x)
            {
                // Will loop through up the inheritance chain until reaches the end or the index is found, used
                // to handle inheritance as topologically sorted array may not have the subclasses listed
                var documentType = x.DocumentType;
                var index = 0;

                do
                {
                    index = _topologicallyOrderedTypes.IndexOf(documentType);
                    documentType = documentType.BaseType;
                } while (index == -1 && documentType != null);

                return index;
            }
        }

        public bool HasOutstandingWork()
        {
            return _operations.Any();
        }

        public bool TryFindStream(string streamKey, out EventStream stream)
        {
            stream = _operations.OfType<AppendEventsOperation>()
                .FirstOrDefault(x => x.Stream.Key == streamKey)?.Stream;

            return stream != null;
        }

        public bool TryFindStream(Guid streamId, out EventStream stream)
        {
            stream = _operations.OfType<AppendEventsOperation>()
                .FirstOrDefault(x => x.Stream.Id == streamId)?.Stream;

            return stream != null;
        }
    }
}
