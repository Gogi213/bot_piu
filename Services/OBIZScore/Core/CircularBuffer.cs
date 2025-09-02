using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Services.OBIZScore.Core
{
    /// <summary>
    /// Высокопроизводительный циклический буфер для хранения истории данных
    /// O(1) для добавления, эффективное использование памяти
    /// </summary>
    public class CircularBuffer<T> : IEnumerable<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _tail;
        private int _size;
        private readonly int _capacity;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be greater than 0", nameof(capacity));
                
            _capacity = capacity;
            _buffer = new T[capacity];
            _head = 0;
            _tail = 0;
            _size = 0;
        }

        /// <summary>
        /// Добавляет новый элемент в буфер
        /// </summary>
        public void Add(T item)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;
            
            if (_size < _capacity)
            {
                _size++;
            }
            else
            {
                _tail = (_tail + 1) % _capacity;
            }
        }

        /// <summary>
        /// Количество элементов в буфере
        /// </summary>
        public int Count => _size;

        /// <summary>
        /// Максимальная емкость буфера
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Проверяет, заполнен ли буфер
        /// </summary>
        public bool IsFull => _size == _capacity;

        /// <summary>
        /// Получает последний добавленный элемент
        /// </summary>
        public T Last()
        {
            if (_size == 0)
                throw new InvalidOperationException("Buffer is empty");
                
            return _buffer[(_head - 1 + _capacity) % _capacity];
        }

        /// <summary>
        /// Получает первый элемент в буфере
        /// </summary>
        public T First()
        {
            if (_size == 0)
                throw new InvalidOperationException("Buffer is empty");
                
            return _buffer[_tail];
        }

        /// <summary>
        /// Получает последние N элементов
        /// </summary>
        public IEnumerable<T> TakeLast(int count)
        {
            count = Math.Min(count, _size);
            
            for (int i = 0; i < count; i++)
            {
                int index = (_head - count + i + _capacity) % _capacity;
                yield return _buffer[index];
            }
        }

        /// <summary>
        /// Получает элемент по индексу (0 - самый старый)
        /// </summary>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _size)
                    throw new ArgumentOutOfRangeException(nameof(index));
                    
                return _buffer[(_tail + index) % _capacity];
            }
        }

        /// <summary>
        /// Очищает буфер
        /// </summary>
        public void Clear()
        {
            _head = 0;
            _tail = 0;
            _size = 0;
            
            // Очищаем ссылки для GC (если T - reference type)
            if (!typeof(T).IsValueType)
            {
                Array.Clear(_buffer, 0, _capacity);
            }
        }

        /// <summary>
        /// Преобразует в массив (порядок от старого к новому)
        /// </summary>
        public T[] ToArray()
        {
            var result = new T[_size];
            
            for (int i = 0; i < _size; i++)
            {
                result[i] = _buffer[(_tail + i) % _capacity];
            }
            
            return result;
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _size; i++)
            {
                yield return _buffer[(_tail + i) % _capacity];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
