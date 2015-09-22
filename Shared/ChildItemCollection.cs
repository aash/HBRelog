using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared
{
    // http://www.thomaslevesque.com/2009/06/12/c-parentchild-relationship-and-xml-serialization/

    /// <summary>
    /// Defines the contract for an object that has a parent object
    /// </summary>
    /// <typeparam name="TParent">Type of the parent object</typeparam>
    public interface IChildItem<TParent> where TParent : class
    {
        TParent Parent { get; set; }
    }

    /// <summary>
    /// Collection of child items. This collection automatically set the
    /// Parent property of the child items when they are added or removed
    /// </summary>
    /// <typeparam name="TParent">Type of the parent object</typeparam>
    /// <typeparam name="T">Type of the child items</typeparam>
    public class ChildItemCollection<TParent, T> : IList<T>
        where TParent : class
        where T : IChildItem<TParent>
    {
        private readonly TParent _parent;
        private readonly IList<T> _collection;

        public ChildItemCollection(TParent parent)
        {
            _parent = parent;
            _collection = new List<T>();
        }

        public ChildItemCollection(TParent parent, IList<T> collection)
        {
            _parent = parent;
            _collection = collection;
        }

        #region IList<T> Members

        public int IndexOf(T item)
        {
            return _collection.IndexOf(item);
        }

        public void Insert(int index, T item)
        {
            if (item != null)
                item.Parent = _parent;
            _collection.Insert(index, item);
        }

        public void RemoveAt(int index)
        {
            T oldItem = _collection[index];
            _collection.RemoveAt(index);
            if (oldItem != null)
                oldItem.Parent = null;
        }

        public T this[int index]
        {
            get
            {
                return _collection[index];
            }
            set
            {
                T oldItem = _collection[index];
                if (value != null)
                    value.Parent = _parent;
                _collection[index] = value;
                if (oldItem != null)
                    oldItem.Parent = null;
            }
        }

        #endregion

        #region ICollection<T> Members

        public void Add(T item)
        {
            if (item != null)
                item.Parent = _parent;
            _collection.Add(item);
        }

        public void Clear()
        {
            foreach (T item in _collection)
            {
                if (item != null)
                    item.Parent = null;
            }
            _collection.Clear();
        }

        public bool Contains(T item)
        {
            return _collection.Contains(item);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            _collection.CopyTo(array, arrayIndex);
        }

        public int Count
        {
            get { return _collection.Count; }
        }

        public bool IsReadOnly
        {
            get { return _collection.IsReadOnly; }
        }

        public bool Remove(T item)
        {
            bool b = _collection.Remove(item);
            if (item != null)
                item.Parent = null;
            return b;
        }

        #endregion

        #region IEnumerable<T> Members

        public IEnumerator<T> GetEnumerator()
        {
            return _collection.GetEnumerator();
        }

        #endregion

        #region IEnumerable Members

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return (_collection as System.Collections.IEnumerable).GetEnumerator();
        }

        #endregion
    }
}
