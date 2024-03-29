﻿using Store.Domain.Interfaces;

namespace Store.Domain.Entities;

public class Order : IEntity<int>
{

    public DateTime CreationDate { get; set; }

    public virtual User User { get; set; } = null!;
    public Guid UserId { get; set; }

    public virtual IEnumerable<OrderItem> OrderItems { get; set; } = null!;
    public int Id { get; set; }

}